using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

internal sealed class SchedulingSessionCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _failureRetryBaseDelay;
    private readonly TimeSpan _failureRetryMaxDelay;
    private readonly TimeSpan _failureResetWindow;
    private SchedulingFailureState? _lastFailure;

    public SchedulingSessionCoordinator(
        TimeSpan? failureRetryBaseDelay = null,
        TimeSpan? failureRetryMaxDelay = null,
        TimeSpan? failureResetWindow = null)
    {
        _failureRetryBaseDelay = failureRetryBaseDelay ?? TimeSpan.FromSeconds(5);
        _failureRetryMaxDelay = failureRetryMaxDelay ?? TimeSpan.FromMinutes(1);
        _failureResetWindow = failureResetWindow ?? TimeSpan.FromMinutes(5);

        if (_failureRetryBaseDelay <= TimeSpan.Zero ||
            _failureRetryMaxDelay < _failureRetryBaseDelay ||
            _failureResetWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureRetryBaseDelay),
                "Failure retry timing must be positive and the maximum delay cannot be shorter than the base delay.");
        }
    }

    public SchedulingSessionIdentity? ActiveSession { get; private set; }

    public Guid? ActiveTransactionId { get; private set; }

    public bool RequiresTransition(SchedulingSessionIdentity? requestedSession) =>
        ActiveSession is not null && ActiveSession != requestedSession;

    public bool IsCurrent(SchedulingSessionIdentity requestedSession) =>
        requestedSession == ActiveSession;

    public void MarkActive(SchedulingSessionIdentity session, Guid transactionId)
    {
        ActiveSession = session;
        ActiveTransactionId = transactionId;
        if (_lastFailure?.Session == session)
        {
            _lastFailure = null;
        }
    }

    public SchedulingFailureState MarkFailed(
        SchedulingSessionIdentity session,
        DateTimeOffset failedAt)
    {
        var consecutiveFailureCount = _lastFailure is not null &&
                                      _lastFailure.Session == session &&
                                      failedAt - _lastFailure.FailedAt <= _failureResetWindow
            ? _lastFailure.ConsecutiveFailureCount + 1
            : 1;
        var exponent = Math.Min(consecutiveFailureCount - 1, 10);
        var multiplier = Math.Min(
            1L << exponent,
            _failureRetryMaxDelay.Ticks / _failureRetryBaseDelay.Ticks);
        var retryDelayTicks = _failureRetryBaseDelay.Ticks * multiplier;
        _lastFailure = new SchedulingFailureState(
            session,
            consecutiveFailureCount,
            failedAt,
            failedAt.AddTicks(retryDelayTicks));
        return _lastFailure;
    }

    public SchedulingFailureState? GetActiveFailureCooldown(
        SchedulingSessionIdentity session,
        DateTimeOffset now) =>
        _lastFailure is not null &&
        _lastFailure.Session == session &&
        now < _lastFailure.RetryAfter
            ? _lastFailure
            : null;

    public void ClearActive()
    {
        ActiveSession = null;
        ActiveTransactionId = null;
    }

    public void Reset()
    {
        ClearActive();
        _lastFailure = null;
    }

    public async Task<T> RunExclusiveAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed record SchedulingFailureState(
    SchedulingSessionIdentity Session,
    int ConsecutiveFailureCount,
    DateTimeOffset FailedAt,
    DateTimeOffset RetryAfter);

internal sealed record SchedulingSessionIdentity(
    int ProcessId,
    DateTimeOffset? ProcessStartedAt,
    string ProcessName,
    string? ExecutablePath,
    string ProfileId,
    int ProfileVersion,
    string ProfileSignature,
    PowerSourceMode PowerSourceMode)
{
    public static SchedulingSessionIdentity Create(
        FocusedAppContext app,
        PerformanceProfile profile,
        PowerSourceMode powerSourceMode)
    {
        var serializedProfile = JsonSerializer.Serialize(profile);
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedProfile)));
        return new SchedulingSessionIdentity(
            app.ProcessId,
            app.ProcessStartedAt,
            app.ProcessName,
            app.ExecutablePath,
            profile.Id,
            profile.Version,
            signature,
            powerSourceMode);
    }
}
