using System.Diagnostics;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

public sealed class TelemetryCoordinator
{
    private readonly IReadOnlyList<ITelemetryProvider> _providers;
    private readonly TelemetryCollectionBudget _budget;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _samplingSync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TelemetryProviderCapabilities> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    public TelemetryCoordinator(
        IEnumerable<ITelemetryProvider> providers,
        TelemetryCollectionBudget? budget = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
        _budget = budget ?? TelemetryCollectionBudget.Default;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        if (_budget.MinimumSamplingInterval < TimeSpan.Zero ||
            _budget.MaximumProviderDuration <= TimeSpan.Zero ||
            _budget.MaximumObservationDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "Telemetry budgets must use non-negative intervals and positive timeouts.");
        }

        var duplicateProvider = _providers
            .GroupBy(provider => provider.ProviderId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateProvider is not null)
        {
            throw new ArgumentException($"Telemetry provider ID '{duplicateProvider.Key}' is duplicated.", nameof(providers));
        }
    }

    public async Task<TelemetryObservation> ObserveAsync(
        FocusedAppContext target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var startedAt = _utcNow();
        var stopwatch = Stopwatch.StartNew();
        var samples = new List<TelemetryProviderSample>(_providers.Count);
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingBudget = _budget.MaximumObservationDuration - stopwatch.Elapsed;
            if (remainingBudget <= TimeSpan.Zero)
            {
                samples.Add(CreateStatusSample(
                    provider.ProviderId,
                    TelemetryCollectionStatus.BudgetExceeded,
                    TelemetryReasonCodes.TotalBudgetExceeded));
                continue;
            }

            var providerTimeout = remainingBudget < _budget.MaximumProviderDuration
                ? remainingBudget
                : _budget.MaximumProviderDuration;
            samples.Add(await ObserveProviderAsync(
                provider,
                target,
                providerTimeout,
                cancellationToken));
        }

        stopwatch.Stop();
        return new TelemetryObservation
        {
            StartedAt = startedAt,
            TotalDuration = stopwatch.Elapsed,
            Target = target,
            ProviderSamples = samples
        };
    }

    public void InvalidateCapabilities(string? providerId = null)
    {
        lock (_samplingSync)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                _capabilities.Clear();
                return;
            }

            _capabilities.Remove(providerId);
        }
    }

    private async Task<TelemetryProviderSample> ObserveProviderAsync(
        ITelemetryProvider provider,
        FocusedAppContext target,
        TimeSpan providerTimeout,
        CancellationToken cancellationToken)
    {
        var providerStopwatch = Stopwatch.StartNew();
        TelemetryProviderCapabilities capabilities;
        try
        {
            lock (_samplingSync)
            {
                _capabilities.TryGetValue(provider.ProviderId, out capabilities!);
            }

            if (capabilities is null)
            {
                capabilities = await RunWithTimeoutAsync(
                    token => provider.GetCapabilitiesAsync(token),
                    providerTimeout,
                    cancellationToken);
                lock (_samplingSync)
                {
                    _capabilities[provider.ProviderId] = capabilities;
                }
            }
        }
        catch (TimeoutException)
        {
            return CreateStatusSample(provider.ProviderId, TelemetryCollectionStatus.TimedOut, TelemetryReasonCodes.CapabilityTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateStatusSample(
                provider.ProviderId,
                TelemetryCollectionStatus.Failed,
                TelemetryReasonCodes.CapabilityFailed,
                exception.Message);
        }

        if (!capabilities.IsSupported)
        {
            return CreateStatusSample(
                provider.ProviderId,
                TelemetryCollectionStatus.Unavailable,
                string.IsNullOrWhiteSpace(capabilities.UnavailableReasonCode)
                    ? TelemetryReasonCodes.ProviderUnsupported
                    : capabilities.UnavailableReasonCode);
        }

        var minimumInterval = capabilities.MinimumSamplingInterval > _budget.MinimumSamplingInterval
            ? capabilities.MinimumSamplingInterval
            : _budget.MinimumSamplingInterval;
        var attemptKey = $"{provider.ProviderId}|{target.ProcessId}";
        var now = _utcNow();
        lock (_samplingSync)
        {
            if (_lastAttempts.TryGetValue(attemptKey, out var lastAttempt) && now - lastAttempt < minimumInterval)
            {
                return CreateStatusSample(
                    provider.ProviderId,
                    TelemetryCollectionStatus.RateLimited,
                    TelemetryReasonCodes.MinimumIntervalNotElapsed);
            }

            _lastAttempts[attemptKey] = now;
        }

        var collectionTimeout = providerTimeout - providerStopwatch.Elapsed;
        if (collectionTimeout <= TimeSpan.Zero)
        {
            return CreateStatusSample(provider.ProviderId, TelemetryCollectionStatus.TimedOut, TelemetryReasonCodes.CollectionTimeout);
        }

        try
        {
            return await RunWithTimeoutAsync(
                token => provider.CollectAsync(target, token),
                collectionTimeout,
                cancellationToken);
        }
        catch (TimeoutException)
        {
            return CreateStatusSample(provider.ProviderId, TelemetryCollectionStatus.TimedOut, TelemetryReasonCodes.CollectionTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateStatusSample(
                provider.ProviderId,
                TelemetryCollectionStatus.Failed,
                TelemetryReasonCodes.CollectionFailed,
                exception.Message);
        }
    }

    private async Task<T> RunWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await operation(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private TelemetryProviderSample CreateStatusSample(
        string providerId,
        TelemetryCollectionStatus status,
        string reasonCode,
        string technicalMessage = "") =>
        new()
        {
            ProviderId = providerId,
            CollectedAt = _utcNow(),
            Status = status,
            ReasonCode = reasonCode,
            TechnicalMessage = technicalMessage
        };
}
