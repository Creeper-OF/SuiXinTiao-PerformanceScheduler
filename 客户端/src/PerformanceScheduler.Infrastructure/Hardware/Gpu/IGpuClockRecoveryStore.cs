namespace PerformanceScheduler.Infrastructure.Hardware.Gpu;

/// <summary>Tracks the intent to restore driver defaults after this provider writes locked clocks.</summary>
public interface IGpuClockRecoveryStore
{
    Task<bool> IsPendingAsync(CancellationToken cancellationToken = default);

    Task MarkPendingAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
