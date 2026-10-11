using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IProcessJobObjectManager
{
    Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProcessIdentitySnapshot?>(null);

    Task<bool> IsSameProcessAsync(
        ProcessIdentitySnapshot identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<bool> ApplyProcessLimitsAsync(
        int processId,
        JobObjectLimitPreference limits,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveProcessLimitsAsync(
        int processId,
        CancellationToken cancellationToken = default);

    Task<JobObjectLimitSnapshot?> GetCurrentLimitsAsync(
        int processId,
        CancellationToken cancellationToken = default);

    Task<ConditionalProcessUpdateResult> RestoreProcessLimitsIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        CancellationToken cancellationToken = default) =>
        RestoreProcessLimitsIfUnchangedAsync(identity, null, cancellationToken);

    async Task<ConditionalProcessUpdateResult> RestoreProcessLimitsIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        JobObjectLimitSnapshot? expectedCurrentLimits,
        CancellationToken cancellationToken = default)
    {
        if (!await IsSameProcessAsync(identity, cancellationToken))
        {
            return ConditionalProcessUpdateResult.IdentityMismatch;
        }

        var current = await GetCurrentLimitsAsync(identity.ProcessId, cancellationToken);
        if (current is null)
        {
            return ConditionalProcessUpdateResult.Failed;
        }

        if (expectedCurrentLimits is not null && !AreLimitsEqual(current, expectedCurrentLimits))
        {
            return ConditionalProcessUpdateResult.CurrentValueMismatch;
        }

        // If no limits are active, nothing to remove
        if (!current.HasCpuRateControl && !current.HasMemoryLimit)
        {
            return ConditionalProcessUpdateResult.Applied;
        }

        return await RemoveProcessLimitsAsync(identity.ProcessId, cancellationToken)
            ? ConditionalProcessUpdateResult.Applied
            : ConditionalProcessUpdateResult.Failed;
    }

    private static bool AreLimitsEqual(JobObjectLimitSnapshot a, JobObjectLimitSnapshot b)
    {
        if (a.HasCpuRateControl != b.HasCpuRateControl) return false;
        if (a.HasCpuRateControl)
        {
            if (a.CpuRateHardCap != b.CpuRateHardCap) return false;
            if (Math.Abs((a.CpuRatePercent ?? 0) - (b.CpuRatePercent ?? 0)) > 0.001) return false;
        }
        if (a.HasMemoryLimit != b.HasMemoryLimit) return false;
        if (a.HasMemoryLimit)
        {
            if (a.MaxCommitMemoryBytes != b.MaxCommitMemoryBytes) return false;
        }
        return true;
    }
}
