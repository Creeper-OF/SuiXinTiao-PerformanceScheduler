using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.App.Services.Telemetry;

public enum ForegroundTelemetryObservationStatus
{
    Collected,
    ForegroundUnavailable,
    ProcessUnavailable
}

public sealed record ForegroundTelemetryObservationResult
{
    public ForegroundTelemetryObservationStatus Status { get; init; }

    public FocusedAppContext? Target { get; init; }

    public TelemetryObservation? Observation { get; init; }

    public bool? HistoryRecorded { get; init; }
}

public sealed class ForegroundTelemetryObservationService
{
    private readonly IForegroundWatcher _foregroundWatcher;
    private readonly IProcessInspector _processInspector;
    private readonly TelemetryCoordinator _coordinator;
    private readonly ITelemetryHistoryStore? _historyStore;
    private readonly IAppLogger? _logger;
    private readonly TelemetryHistoryRetentionPolicy _retentionPolicy;

    public ForegroundTelemetryObservationService(
        IForegroundWatcher foregroundWatcher,
        IProcessInspector processInspector,
        TelemetryCoordinator coordinator,
        ITelemetryHistoryStore? historyStore = null,
        IAppLogger? logger = null,
        TelemetryHistoryRetentionPolicy? retentionPolicy = null)
    {
        _foregroundWatcher = foregroundWatcher;
        _processInspector = processInspector;
        _coordinator = coordinator;
        _historyStore = historyStore;
        _logger = logger;
        _retentionPolicy = (retentionPolicy ?? TelemetryHistoryRetentionPolicy.Default).Normalize();
    }

    public async Task<ForegroundTelemetryObservationResult> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var foreground = await _foregroundWatcher.GetForegroundWindowAsync(cancellationToken);
        if (foreground is null)
        {
            return new ForegroundTelemetryObservationResult
            {
                Status = ForegroundTelemetryObservationStatus.ForegroundUnavailable
            };
        }

        var target = await _processInspector.InspectAsync(foreground, cancellationToken);
        if (target is null)
        {
            return new ForegroundTelemetryObservationResult
            {
                Status = ForegroundTelemetryObservationStatus.ProcessUnavailable
            };
        }

        var observation = await _coordinator.ObserveAsync(target, cancellationToken);
        bool? historyRecorded = null;
        if (_historyStore is not null)
        {
            try
            {
                await _historyStore.RecordObservationAsync(observation, cancellationToken);
                historyRecorded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                historyRecorded = false;
                _logger?.Error("Telemetry observation history could not be persisted.", exception);
            }

            if (historyRecorded == true)
            {
                try
                {
                    await _historyStore.PruneAsync(_retentionPolicy, cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger?.Error("Telemetry observation history cleanup failed.", exception);
                }
            }
        }

        return new ForegroundTelemetryObservationResult
        {
            Status = ForegroundTelemetryObservationStatus.Collected,
            Target = target,
            Observation = observation,
            HistoryRecorded = historyRecorded
        };
    }

    public Task<IReadOnlyList<TelemetryObservationHistoryEntry>> LoadRecentAsync(
        int count = 20,
        CancellationToken cancellationToken = default) =>
        _historyStore?.LoadRecentAsync(count, cancellationToken) ??
        Task.FromResult<IReadOnlyList<TelemetryObservationHistoryEntry>>([]);

    public void InvalidateProviderCapabilities(string? providerId = null) =>
        _coordinator.InvalidateCapabilities(providerId);
}
