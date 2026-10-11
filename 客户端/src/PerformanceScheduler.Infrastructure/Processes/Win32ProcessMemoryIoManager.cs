using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;

namespace PerformanceScheduler.Infrastructure.Processes;

public sealed class Win32ProcessMemoryIoManager : IProcessMemoryIoManager
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetInformation = 0x0200;

    private const int ProcessIoPriorityInfoClass = 33; // ProcessIoPriority in ntosp.h / ntdll
    private const int ProcessMemoryPriorityInfoClass = 0; // ProcessMemoryPriority in PROCESS_INFORMATION_CLASS

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriorityInformation
    {
        public uint MemoryPriority;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        SafeProcessHandle hProcess,
        int processInformationClass,
        out MemoryPriorityInformation processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        SafeProcessHandle hProcess,
        int processInformationClass,
        ref MemoryPriorityInformation processInformation,
        uint processInformationSize);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        out int processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        ref int processInformation,
        int processInformationLength);

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

    public Task<IoPriorityLevel?> TryGetIoPriorityAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupported() || processId <= 0)
        {
            return Task.FromResult<IoPriorityLevel?>(null);
        }

        try
        {
            using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
            {
                return Task.FromResult<IoPriorityLevel?>(null);
            }

            var status = NtQueryInformationProcess(handle, ProcessIoPriorityInfoClass, out var rawValue, sizeof(int), out _);
            if (status != 0)
            {
                return Task.FromResult<IoPriorityLevel?>(null);
            }

            return Task.FromResult(MapIoPriorityFromNative(rawValue));
        }
        catch
        {
            return Task.FromResult<IoPriorityLevel?>(null);
        }
    }

    public Task<bool> SetIoPriorityAsync(
        int processId,
        IoPriorityLevel level,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupported() || processId <= 0)
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

            using var handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
            {
                return Task.FromResult(false);
            }

            var rawValue = MapIoPriorityToNative(level);
            var setStatus = NtSetInformationProcess(handle, ProcessIoPriorityInfoClass, ref rawValue, sizeof(int));
            if (setStatus != 0)
            {
                return Task.FromResult(false);
            }

            // Read-back verification
            var queryStatus = NtQueryInformationProcess(handle, ProcessIoPriorityInfoClass, out var readBack, sizeof(int), out _);
            if (queryStatus != 0)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(MapIoPriorityFromNative(readBack) == level);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<MemoryPriorityLevel?> TryGetMemoryPriorityAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupported() || processId <= 0)
        {
            return Task.FromResult<MemoryPriorityLevel?>(null);
        }

        try
        {
            using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
            {
                return Task.FromResult<MemoryPriorityLevel?>(null);
            }

            if (!GetProcessInformation(
                    handle,
                    ProcessMemoryPriorityInfoClass,
                    out var memInfo,
                    (uint)Marshal.SizeOf<MemoryPriorityInformation>()))
            {
                return Task.FromResult<MemoryPriorityLevel?>(null);
            }

            return Task.FromResult(MapMemoryPriorityFromNative(memInfo.MemoryPriority));
        }
        catch
        {
            return Task.FromResult<MemoryPriorityLevel?>(null);
        }
    }

    public Task<bool> SetMemoryPriorityAsync(
        int processId,
        MemoryPriorityLevel level,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupported() || processId <= 0)
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

            using var handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
            {
                return Task.FromResult(false);
            }

            var memInfo = new MemoryPriorityInformation
            {
                MemoryPriority = MapMemoryPriorityToNative(level)
            };

            if (!SetProcessInformation(
                    handle,
                    ProcessMemoryPriorityInfoClass,
                    ref memInfo,
                    (uint)Marshal.SizeOf<MemoryPriorityInformation>()))
            {
                return Task.FromResult(false);
            }

            // Read-back verification
            if (!GetProcessInformation(
                    handle,
                    ProcessMemoryPriorityInfoClass,
                    out var readBackInfo,
                    (uint)Marshal.SizeOf<MemoryPriorityInformation>()))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(MapMemoryPriorityFromNative(readBackInfo.MemoryPriority) == level);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public static bool IsSupported() =>
        OperatingSystem.IsWindowsVersionAtLeast(6, 2); // Windows 8 / Server 2012+

    private static int MapIoPriorityToNative(IoPriorityLevel level) =>
        level switch
        {
            IoPriorityLevel.VeryLow => 0,
            IoPriorityLevel.Low => 1,
            IoPriorityLevel.Normal => 2,
            _ => 2
        };

    private static IoPriorityLevel? MapIoPriorityFromNative(int rawValue) =>
        rawValue switch
        {
            0 => IoPriorityLevel.VeryLow,
            1 => IoPriorityLevel.Low,
            2 => IoPriorityLevel.Normal,
            _ => null
        };

    private static uint MapMemoryPriorityToNative(MemoryPriorityLevel level) =>
        level switch
        {
            MemoryPriorityLevel.Lowest => 1,
            MemoryPriorityLevel.VeryLow => 1,
            MemoryPriorityLevel.Low => 2,
            MemoryPriorityLevel.Medium => 3,
            MemoryPriorityLevel.BelowNormal => 4,
            MemoryPriorityLevel.Normal => 5,
            _ => 5
        };

    private static MemoryPriorityLevel? MapMemoryPriorityFromNative(uint rawValue) =>
        rawValue switch
        {
            1 => MemoryPriorityLevel.VeryLow,
            2 => MemoryPriorityLevel.Low,
            3 => MemoryPriorityLevel.Medium,
            4 => MemoryPriorityLevel.BelowNormal,
            5 => MemoryPriorityLevel.Normal,
            _ => null
        };
}
