using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IPowerEmergencyGate
{
    bool IsThrottled { get; }

    Task<bool> HandlePowerTransitionAsync(
        PowerSourceMode previousMode,
        PowerSourceMode currentMode,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(CancellationToken cancellationToken = default);

    // Includes pending provider recovery from a previous application session.
    Task<bool> RecoverAsync(CancellationToken cancellationToken = default);

    // Owns the complete lock/reset pair so power changes cannot interleave it.
    Task<GpuLockSelfTestResult> TestGpuClockLockAndResetAsync(
        uint minClockMHz = 210,
        uint maxClockMHz = 800,
        CancellationToken cancellationToken = default);
}
