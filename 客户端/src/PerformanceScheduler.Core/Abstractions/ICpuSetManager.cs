using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface ICpuSetManager
{
    Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProcessIdentitySnapshot?>(null);

    Task<bool> IsSameProcessAsync(
        ProcessIdentitySnapshot identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<IReadOnlyList<uint>?> TryGetCpuSetsAsync(
        int processId,
        CancellationToken cancellationToken = default);

    Task<bool> SetCpuSetsAsync(
        int processId,
        IReadOnlyList<uint>? cpuSetIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<uint>> GetSystemCpuSetIdsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<uint>>(Array.Empty<uint>());

    async Task<ConditionalProcessUpdateResult> RestoreCpuSetsIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        IReadOnlyList<uint> expectedCurrentCpuSets,
        IReadOnlyList<uint> originalCpuSets,
        CancellationToken cancellationToken = default)
    {
        if (!await IsSameProcessAsync(identity, cancellationToken))
        {
            return ConditionalProcessUpdateResult.IdentityMismatch;
        }

        var current = await TryGetCpuSetsAsync(identity.ProcessId, cancellationToken);
        if (current is null)
        {
            return ConditionalProcessUpdateResult.Failed;
        }

        if (!AreEquivalent(current, expectedCurrentCpuSets))
        {
            return ConditionalProcessUpdateResult.CurrentValueMismatch;
        }

        return await SetCpuSetsAsync(identity.ProcessId, originalCpuSets, cancellationToken)
            ? ConditionalProcessUpdateResult.Applied
            : ConditionalProcessUpdateResult.Failed;
    }

    private static bool AreEquivalent(IReadOnlyList<uint> first, IReadOnlyList<uint> second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first.Count != second.Count)
        {
            return false;
        }

        return new HashSet<uint>(first).SetEquals(second);
    }
}