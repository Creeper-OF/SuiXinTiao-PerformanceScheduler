using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Coordinates collection of background candidates and contention evidence to
/// produce an opt-in, evidence-backed ResourceArbitrationSummary.
/// Never mutates process state or invents candidates without evidence.
/// </summary>
public sealed class ResourceArbitrationProvider : IResourceArbitrationProvider
{
    private readonly IResourceArbitrationCandidateProvider _candidateProvider;
    private readonly ResourceArbitrationEvaluator _arbitrationEvaluator;
    private readonly ITelemetryHistoryStore? _historyStore;
    private readonly Func<FocusedAppContext, CancellationToken, Task<ResourceContentionSummary?>>? _contentionProvider;
    private readonly IAppLogger? _logger;

    public ResourceArbitrationProvider(
        IResourceArbitrationCandidateProvider candidateProvider,
        ResourceArbitrationEvaluator? arbitrationEvaluator = null,
        ITelemetryHistoryStore? historyStore = null,
        Func<FocusedAppContext, CancellationToken, Task<ResourceContentionSummary?>>? contentionProvider = null,
        IAppLogger? logger = null)
    {
        _candidateProvider = candidateProvider ?? throw new ArgumentNullException(nameof(candidateProvider));
        _arbitrationEvaluator = arbitrationEvaluator ?? new ResourceArbitrationEvaluator();
        _historyStore = historyStore;
        _contentionProvider = contentionProvider;
        _logger = logger;
    }

    public async Task<ResourceArbitrationSummary?> GetArbitrationSummaryAsync(
        FocusedAppContext foregroundApp,
        PerformanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(foregroundApp);
        ArgumentNullException.ThrowIfNull(profile);

        if (!profile.Priority.UseResourceArbitration)
        {
            return null;
        }

        IReadOnlyList<ResourceArbitrationCandidate> candidates;
        try
        {
            candidates = await _candidateProvider.CollectAsync(
                foregroundApp,
                profile,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.Error(
                $"Resource arbitration candidate collection failed for {foregroundApp.ProcessName}.",
                exception);
            return null;
        }

        ResourceContentionSummary? contention = null;
        if (_contentionProvider is not null)
        {
            try
            {
                contention = await _contentionProvider(foregroundApp, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger?.Error(
                    $"Resource contention provider failed for {foregroundApp.ProcessName}.",
                    exception);
            }
        }

        if (contention is null && _historyStore is not null)
        {
            try
            {
                var recentHistory = await _historyStore.LoadRecentAsync(
                    count: 10,
                    cancellationToken: cancellationToken);

                var latestMatching = recentHistory
                    .Where(entry => entry.Observation.Target.ProcessId == foregroundApp.ProcessId &&
                                    string.Equals(entry.Observation.Target.ProcessName, foregroundApp.ProcessName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(entry => entry.Observation.StartedAt)
                    .FirstOrDefault();

                contention = latestMatching?.ResourceContention;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger?.Error(
                    $"Loading telemetry history for resource contention failed for {foregroundApp.ProcessName}.",
                    exception);
            }
        }

        contention ??= new ResourceContentionSummary
        {
            Level = ResourceContentionLevel.Unknown
        };

        return _arbitrationEvaluator.Evaluate(
            contention,
            foregroundApp.ProcessId,
            candidates,
            enabled: true);
    }
}
