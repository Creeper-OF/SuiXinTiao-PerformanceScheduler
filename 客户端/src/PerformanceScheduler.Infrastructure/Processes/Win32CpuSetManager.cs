using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;

namespace PerformanceScheduler.Infrastructure.Processes;

public sealed class Win32CpuSetManager : ICpuSetManager
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetLimitedInformation = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessDefaultCpuSets(
        SafeProcessHandle processHandle,
        [Out] uint[]? cpuSetIdList,
        uint cpuSetIdCount,
        out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDefaultCpuSets(
        SafeProcessHandle processHandle,
        [In] uint[]? cpuSetIdList,
        uint cpuSetIdCount);

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

        try
        {
            using var process = Process.GetProcessById(processId);
            return Task.FromResult<ProcessIdentitySnapshot?>(new ProcessIdentitySnapshot
            {
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                StartedAt = process.StartTime
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
               current.StartedAt == identity.StartedAt;
    }

    public Task<IReadOnlyList<uint>?> TryGetCpuSetsAsync(int processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupported() || processId <= 0)
        {
            return Task.FromResult<IReadOnlyList<uint>?>(null);
        }

        try
        {
            using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
            {
                return Task.FromResult<IReadOnlyList<uint>?>(null);
            }

            if (!GetProcessDefaultCpuSets(handle, null, 0, out var count))
            {
                return Task.FromResult<IReadOnlyList<uint>?>(null);
            }

            if (count == 0)
            {
                return Task.FromResult<IReadOnlyList<uint>?>(Array.Empty<uint>());
            }

            var buffer = new uint[count];
            if (!GetProcessDefaultCpuSets(handle, buffer, count, out _))
            {
                return Task.FromResult<IReadOnlyList<uint>?>(null);
            }

            return Task.FromResult<IReadOnlyList<uint>?>(buffer);
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<uint>?>(null);
        }
    }

    public Task<bool> SetCpuSetsAsync(int processId, IReadOnlyList<uint>? cpuSetIds, CancellationToken cancellationToken = default)
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

            using var handle = OpenProcess(ProcessSetLimitedInformation | ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
            {
                return Task.FromResult(false);
            }

            var array = cpuSetIds is not null && cpuSetIds.Count > 0
                ? cpuSetIds.ToArray()
                : Array.Empty<uint>();

            var success = SetProcessDefaultCpuSets(handle, array.Length == 0 ? null : array, (uint)array.Length);
            if (!success)
            {
                return Task.FromResult(false);
            }

            // Read-back verification
            if (GetProcessDefaultCpuSets(handle, null, 0, out var count))
            {
                var readBack = new uint[count];
                if (count == 0 || GetProcessDefaultCpuSets(handle, readBack, count, out _))
                {
                    return Task.FromResult(new HashSet<uint>(readBack).SetEquals(array));
                }
            }

            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public static bool IsSupported() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393);
}