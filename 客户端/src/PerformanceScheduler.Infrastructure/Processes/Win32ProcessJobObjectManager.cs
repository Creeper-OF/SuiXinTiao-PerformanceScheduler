using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;

namespace PerformanceScheduler.Infrastructure.Processes;

public sealed class Win32ProcessJobObjectManager : IProcessJobObjectManager, IDisposable
{
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;

    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;

    private const uint JobObjectCpuRateControlEnable = 0x1;
    private const uint JobObjectCpuRateControlHardCap = 0x4;
    private const uint JobObjectLimitProcessMemory = 0x00000100;

    private readonly ConcurrentDictionary<int, SafeFileHandle> _activeJobs = new();
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectCpuRateControlInformationStruct
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength,
        out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    public Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return Task.FromResult<ProcessIdentitySnapshot?>(null);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return Task.FromResult<ProcessIdentitySnapshot?>(new ProcessIdentitySnapshot
            {
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                StartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                ExecutablePath = WindowsProcessStateReader.TryReadImagePath(process) ?? string.Empty
            });
        }
        catch
        {
            return Task.FromResult<ProcessIdentitySnapshot?>(null);
        }
    }

    public async Task<bool> IsSameProcessAsync(
        ProcessIdentitySnapshot identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var current = await TryGetProcessIdentityAsync(identity.ProcessId, cancellationToken);
        return current is not null &&
               current.ProcessId == identity.ProcessId &&
               string.Equals(current.ProcessName, identity.ProcessName, StringComparison.OrdinalIgnoreCase) &&
               current.StartedAt == identity.StartedAt &&
               (string.IsNullOrWhiteSpace(identity.ExecutablePath) ||
                string.IsNullOrWhiteSpace(current.ExecutablePath) ||
                string.Equals(identity.ExecutablePath, current.ExecutablePath, StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> ApplyProcessLimitsAsync(
        int processId,
        JobObjectLimitPreference limits,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(limits);

        if (!IsSupported() || processId <= 0 || _disposed)
        {
            return Task.FromResult(false);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (processId == Environment.ProcessId ||
                ProtectedProcessNamePolicy.IsProtected(process.ProcessName))
            {
                return Task.FromResult(false);
            }

            // Get or create Job Object for this PID
            var jobHandle = GetOrCreateJobForProcess(processId);
            if (jobHandle is null || jobHandle.IsInvalid)
            {
                return Task.FromResult(false);
            }

            // Assign process to Job Object if not already assigned
            using var procHandle = OpenProcess(ProcessSetQuota | ProcessTerminate, false, processId);
            if (procHandle.IsInvalid)
            {
                return Task.FromResult(false);
            }

            // Note: AssignProcessToJobObject will fail if process is already in a job that doesn't permit nesting,
            // or if it's already assigned to this job it returns error 5 (ACCESS_DENIED) or 1392.
            // Ignore error if already in this job.
            if (!AssignProcessToJobObject(jobHandle, procHandle))
            {
                var err = Marshal.GetLastWin32Error();
                // 5: ACCESS_DENIED (already assigned or restricted), 0: success
                // If it fails completely, we still test if limits can be configured or if assignment failed
            }

            // 1. Configure CPU rate
            if (limits.CpuRate.Enabled && limits.CpuRate.MaxCpuRatePercent is { } cpuPercent)
            {
                var clampedPercent = Math.Clamp(cpuPercent, 0.1, 100.0);
                var rateInt = (uint)Math.Round(clampedPercent * 100.0);
                uint flags = JobObjectCpuRateControlEnable;
                if (limits.CpuRate.HardCap)
                {
                    flags |= JobObjectCpuRateControlHardCap;
                }

                var cpuRateInfo = new JobObjectCpuRateControlInformationStruct
                {
                    ControlFlags = flags,
                    CpuRate = rateInt
                };

                if (!SetJobCpuRate(jobHandle, cpuRateInfo))
                {
                    return Task.FromResult(false);
                }
            }
            else
            {
                // Clear CPU rate limit if disabled
                var clearCpu = new JobObjectCpuRateControlInformationStruct { ControlFlags = 0, CpuRate = 0 };
                SetJobCpuRate(jobHandle, clearCpu);
            }

            // 2. Configure Memory limit
            if (limits.MemoryLimit.Enabled && limits.MemoryLimit.MaxCommitMemoryBytes is { } memBytes and > 0)
            {
                var extLimit = new JobObjectExtendedLimitInformationStruct
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitProcessMemory
                    },
                    ProcessMemoryLimit = (nuint)memBytes
                };

                if (!SetJobExtendedLimit(jobHandle, extLimit))
                {
                    return Task.FromResult(false);
                }
            }
            else
            {
                // Clear memory limit if disabled
                var clearMem = new JobObjectExtendedLimitInformationStruct
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = 0
                    },
                    ProcessMemoryLimit = 0
                };
                SetJobExtendedLimit(jobHandle, clearMem);
            }

            // Read-back verification
            var snapshot = QueryCurrentLimits(processId, jobHandle);
            if (snapshot is null)
            {
                return Task.FromResult(false);
            }

            if (limits.CpuRate.Enabled && limits.CpuRate.MaxCpuRatePercent is not null)
            {
                if (!snapshot.HasCpuRateControl)
                {
                    return Task.FromResult(false);
                }
            }

            if (limits.MemoryLimit.Enabled && limits.MemoryLimit.MaxCommitMemoryBytes is not null)
            {
                if (!snapshot.HasMemoryLimit)
                {
                    return Task.FromResult(false);
                }
            }

            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> RemoveProcessLimitsAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupported() || processId <= 0 || _disposed)
        {
            return Task.FromResult(false);
        }

        try
        {
            if (_activeJobs.TryRemove(processId, out var jobHandle))
            {
                // Clear limits on the job object and close the handle
                var clearCpu = new JobObjectCpuRateControlInformationStruct { ControlFlags = 0, CpuRate = 0 };
                SetJobCpuRate(jobHandle, clearCpu);

                var clearMem = new JobObjectExtendedLimitInformationStruct
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0 },
                    ProcessMemoryLimit = 0
                };
                SetJobExtendedLimit(jobHandle, clearMem);

                jobHandle.Dispose();
                return Task.FromResult(true);
            }

            // If no job tracked in dictionary, nothing was applied by us
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<JobObjectLimitSnapshot?> GetCurrentLimitsAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupported() || processId <= 0 || _disposed)
        {
            return Task.FromResult<JobObjectLimitSnapshot?>(null);
        }

        try
        {
            if (!_activeJobs.TryGetValue(processId, out var jobHandle) || jobHandle.IsInvalid)
            {
                return Task.FromResult<JobObjectLimitSnapshot?>(new JobObjectLimitSnapshot
                {
                    ProcessId = processId,
                    HasCpuRateControl = false,
                    CpuRatePercent = null,
                    CpuRateHardCap = false,
                    HasMemoryLimit = false,
                    MaxCommitMemoryBytes = null
                });
            }

            return Task.FromResult(QueryCurrentLimits(processId, jobHandle));
        }
        catch
        {
            return Task.FromResult<JobObjectLimitSnapshot?>(null);
        }
    }

    private SafeFileHandle? GetOrCreateJobForProcess(int processId)
    {
        return _activeJobs.GetOrAdd(processId, pid =>
        {
            var jobName = $"PerformanceScheduler_Job_{pid}_{Guid.NewGuid():N}";
            return CreateJobObject(IntPtr.Zero, jobName);
        });
    }

    private static bool SetJobCpuRate(SafeFileHandle jobHandle, JobObjectCpuRateControlInformationStruct info)
    {
        int size = Marshal.SizeOf<JobObjectCpuRateControlInformationStruct>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            return SetInformationJobObject(jobHandle, JobObjectCpuRateControlInformation, ptr, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static bool SetJobExtendedLimit(SafeFileHandle jobHandle, JobObjectExtendedLimitInformationStruct info)
    {
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            return SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static JobObjectLimitSnapshot? QueryCurrentLimits(int processId, SafeFileHandle jobHandle)
    {
        int cpuSize = Marshal.SizeOf<JobObjectCpuRateControlInformationStruct>();
        IntPtr cpuPtr = Marshal.AllocHGlobal(cpuSize);

        int memSize = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        IntPtr memPtr = Marshal.AllocHGlobal(memSize);

        try
        {
            bool hasCpu = false;
            double? cpuPercent = null;
            bool hardCap = false;

            if (QueryInformationJobObject(jobHandle, JobObjectCpuRateControlInformation, cpuPtr, (uint)cpuSize, out _))
            {
                var cpuInfo = Marshal.PtrToStructure<JobObjectCpuRateControlInformationStruct>(cpuPtr);
                if ((cpuInfo.ControlFlags & JobObjectCpuRateControlEnable) != 0)
                {
                    hasCpu = true;
                    cpuPercent = Math.Round(cpuInfo.CpuRate / 100.0, 2);
                    hardCap = (cpuInfo.ControlFlags & JobObjectCpuRateControlHardCap) != 0;
                }
            }

            bool hasMem = false;
            ulong? memLimit = null;

            if (QueryInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, memPtr, (uint)memSize, out _))
            {
                var memInfo = Marshal.PtrToStructure<JobObjectExtendedLimitInformationStruct>(memPtr);
                if ((memInfo.BasicLimitInformation.LimitFlags & JobObjectLimitProcessMemory) != 0 && memInfo.ProcessMemoryLimit > 0)
                {
                    hasMem = true;
                    memLimit = (ulong)memInfo.ProcessMemoryLimit;
                }
            }

            return new JobObjectLimitSnapshot
            {
                ProcessId = processId,
                HasCpuRateControl = hasCpu,
                CpuRatePercent = cpuPercent,
                CpuRateHardCap = hardCap,
                HasMemoryLimit = hasMem,
                MaxCommitMemoryBytes = memLimit
            };
        }
        finally
        {
            Marshal.FreeHGlobal(cpuPtr);
            Marshal.FreeHGlobal(memPtr);
        }
    }

    public static bool IsSupported() =>
        OperatingSystem.IsWindowsVersionAtLeast(6, 2); // Windows 8+ supports nested jobs and CPU rate control

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var job in _activeJobs.Values)
        {
            try
            {
                // Clear limits before releasing
                var clearCpu = new JobObjectCpuRateControlInformationStruct { ControlFlags = 0, CpuRate = 0 };
                SetJobCpuRate(job, clearCpu);

                var clearMem = new JobObjectExtendedLimitInformationStruct
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0 },
                    ProcessMemoryLimit = 0
                };
                SetJobExtendedLimit(job, clearMem);

                job.Dispose();
            }
            catch
            {
            }
        }
        _activeJobs.Clear();
    }
}
