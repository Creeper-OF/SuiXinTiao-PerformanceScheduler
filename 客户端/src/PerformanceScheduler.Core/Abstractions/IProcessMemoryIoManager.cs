using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IProcessMemoryIoManager
{
    Task<ProcessIdentitySnapshot?> TryGetProcessIdentityAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProcessIdentitySnapshot?>(null);

    Task<bool> IsSameProcessAsync(
        ProcessIdentitySnapshot identity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<IoPriorityLevel?> TryGetIoPriorityAsync(
        int processId,
        CancellationToken cancellationToken = default);

    Task<bool> SetIoPriorityAsync(
        int processId,
        IoPriorityLevel level,
        CancellationToken cancellationToken = default);

    Task<MemoryPriorityLevel?> TryGetMemoryPriorityAsync(
        int processId,
        CancellationToken cancellationToken = default);

    Task<bool> SetMemoryPriorityAsync(
        int processId,
        MemoryPriorityLevel level,
        CancellationToken cancellationToken = default);

    async Task<ConditionalProcessUpdateResult> RestoreIoPriorityIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        IoPriorityLevel expectedCurrentLevel,
        IoPriorityLevel originalLevel,
        CancellationToken cancellationToken = default)
    {
        if (!await IsSameProcessAsync(identity, cancellationToken))
        {
            return ConditionalProcessUpdateResult.IdentityMismatch;
        }

        var current = await TryGetIoPriorityAsync(identity.ProcessId, cancellationToken);
        if (current != expectedCurrentLevel)
        {
            return current is null
                ? ConditionalProcessUpdateResult.Failed
                : ConditionalProcessUpdateResult.CurrentValueMismatch;
        }

        return await SetIoPriorityAsync(identity.ProcessId, originalLevel, cancellationToken)
            ? ConditionalProcessUpdateResult.Applied
            : ConditionalProcessUpdateResult.Failed;
    }

    async Task<ConditionalProcessUpdateResult> RestoreMemoryPriorityIfUnchangedAsync(
        ProcessIdentitySnapshot identity,
        MemoryPriorityLevel expectedCurrentLevel,
        MemoryPriorityLevel originalLevel,
        CancellationToken cancellationToken = default)
    {
        if (!await IsSameProcessAsync(identity, cancellationToken))
        {
            return ConditionalProcessUpdateResult.IdentityMismatch;
        }

        var current = await TryGetMemoryPriorityAsync(identity.ProcessId, cancellationToken);
        if (current != expectedCurrentLevel)
        {
            return current is null
                ? ConditionalProcessUpdateResult.Failed
                : ConditionalProcessUpdateResult.CurrentValueMismatch;
        }

        return await SetMemoryPriorityAsync(identity.ProcessId, originalLevel, cancellationToken)
            ? ConditionalProcessUpdateResult.Applied
            : ConditionalProcessUpdateResult.Failed;
    }
}
