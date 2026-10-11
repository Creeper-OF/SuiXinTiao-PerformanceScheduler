using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface ICpuAffinityManager
{
    Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProcessIdentitySnapshot?>(null);

    Task<bool> IsSameProcessAsync(
        ProcessIdentitySnapshot identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<long?> TryGetAffinityAsync(int processId, CancellationToken cancellationToken = default);

    Task<bool> SetAffinityAsync(int processId, long affinityMask, CancellationToken cancellationToken = default);

    async Task<ConditionalProcessUpdateResult> RestoreAffinityIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        long expectedCurrentAffinity,
        long originalAffinity,
        CancellationToken cancellationToken = default)
    {
        if (!await IsSameProcessAsync(identity, cancellationToken))
        {
            return ConditionalProcessUpdateResult.IdentityMismatch;
        }

        var currentAffinity = await TryGetAffinityAsync(identity.ProcessId, cancellationToken);
        if (currentAffinity != expectedCurrentAffinity)
        {
            return currentAffinity is null
                ? ConditionalProcessUpdateResult.Failed
                : ConditionalProcessUpdateResult.CurrentValueMismatch;
        }

        return await SetAffinityAsync(identity.ProcessId, originalAffinity, cancellationToken)
            ? ConditionalProcessUpdateResult.Applied
            : ConditionalProcessUpdateResult.Failed;
    }
}
