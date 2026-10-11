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

    public async Task<IReadOnlyList<TelemetryProviderCapabilities>> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var capabilities = new List<TelemetryProviderCapabilities>(_providers.Count);
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingBudget = _budget.MaximumObservationDuration - stopwatch.Elapsed;
            if (remainingBudget <= TimeSpan.Zero)
            {
                capabilities.Add(CreateUnavailableCapabilities(
                    provider.ProviderId,
                    TelemetryReasonCodes.TotalBudgetExceeded));
                continue;
            }

            var providerTimeout = remainingBudget < _budget.MaximumProviderDuration
                ? remainingBudget
                : _budget.MaximumProviderDuration;
            try
            {
                var capability = await LoadCapabilitiesAsync(
                    provider,
                    providerTimeout,
                    cancellationToken);
                capabilities.Add(string.Equals(
                        capability.ProviderId,
                        provider.ProviderId,
                        StringComparison.OrdinalIgnoreCase)
                    ? capability
                    : CreateUnavailableCapabilities(
                        provider.ProviderId,
                        TelemetryReasonCodes.CapabilityFailed));
            }
            catch (TimeoutException)
            {
                capabilities.Add(CreateUnavailableCapabilities(
                    provider.ProviderId,
                    TelemetryReasonCodes.CapabilityTimeout));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                capabilities.Add(CreateUnavailableCapabilities(
                    provider.ProviderId,
                    TelemetryReasonCodes.CapabilityFailed));
            }
        }

        stopwatch.Stop();
        return capabilities;
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
            capabilities = await LoadCapabilitiesAsync(
                provider,
                providerTimeout,
                cancellationToken);
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

        if (!string.Equals(
                capabilities.ProviderId,
                provider.ProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            return CreateStatusSample(
                provider.ProviderId,
                TelemetryCollectionStatus.Failed,
                TelemetryReasonCodes.CapabilityFailed,
                $"Provider capability ID '{capabilities.ProviderId}' does not match '{provider.ProviderId}'.");
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

    private async Task<TelemetryProviderCapabilities> LoadCapabilitiesAsync(
        ITelemetryProvider provider,
        TimeSpan providerTimeout,
        CancellationToken cancellationToken)
    {
        lock (_samplingSync)
        {
            if (_capabilities.TryGetValue(provider.ProviderId, out var cachedCapabilities))
            {
                return cachedCapabilities;
            }
        }

        var capabilities = await RunWithTimeoutAsync(
            token => provider.GetCapabilitiesAsync(token),
            providerTimeout,
            cancellationToken);
        if (capabilities is null)
        {
            throw new InvalidOperationException("Telemetry provider returned null capabilities.");
        }

        lock (_samplingSync)
        {
            _capabilities[provider.ProviderId] = capabilities;
        }

        return capabilities;
    }

    private static TelemetryProviderCapabilities CreateUnavailableCapabilities(
        string providerId,
        string reasonCode) =>
        new()
        {
            ProviderId = providerId,
            IsSupported = false,
            UnavailableReasonCode = reasonCode
        };

    private async Task<T> RunWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var operationTask = operation(timeoutCts.Token) ??
                throw new InvalidOperationException("Telemetry operation returned a null task.");
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completedTask = await Task.WhenAny(operationTask, timeoutTask).ConfigureAwait(false);
            if (completedTask == operationTask)
            {
                return await operationTask.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            timeoutCts.Cancel();
            _ = ObserveCompletionAsync(operationTask, timeoutCts);
            timeoutCts = null;
            throw new TimeoutException();
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private static async Task ObserveCompletionAsync<T>(
        Task<T> operationTask,
        CancellationTokenSource timeoutCts)
    {
        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch
        {
            // The coordinator already returned a stable timeout result; consume late provider failures.
        }
        finally
        {
            timeoutCts.Dispose();
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
