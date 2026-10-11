using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;

namespace PerformanceScheduler.Infrastructure.Processes;

public sealed class Win32ProcessAffinityManager : ICpuAffinityManager
{
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

    public Task<long?> TryGetAffinityAsync(int processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return Task.FromResult<long?>(null);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return Task.FromResult<long?>((long)process.ProcessorAffinity);
        }
        catch
        {
            return Task.FromResult<long?>(null);
        }
    }

    public Task<bool> SetAffinityAsync(int processId, long affinityMask, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows() || processId <= 0 || affinityMask <= 0)
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

            var systemMask = GetSystemMask();
            var validMask = affinityMask & systemMask;
            if (validMask <= 0)
            {
                return Task.FromResult(false);
            }

            process.ProcessorAffinity = (nint)validMask;
            return Task.FromResult((long)process.ProcessorAffinity == validMask);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static long GetSystemMask()
    {
        var count = Math.Clamp(Environment.ProcessorCount, 1, 64);
        return count >= 64 ? -1L : (1L << count) - 1;
    }
}
