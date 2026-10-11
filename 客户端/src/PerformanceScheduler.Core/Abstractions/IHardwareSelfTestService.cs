using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IHardwareSelfTestService
{
    Task<GpuLockSelfTestResult> TestGpuClockLockAndResetAsync(
        uint minClockMHz = 210,
        uint maxClockMHz = 800,
        CancellationToken cancellationToken = default);

    Task<PowerEmergencyGateSelfTestResult> TestPowerEmergencyGateSimulationAsync(
        CancellationToken cancellationToken = default);

    Task<PowerPlanSelfTestResult> TestPowerPlanQueryAsync(
        CancellationToken cancellationToken = default);

    Task<ProcessSafetySelfTestResult> TestProcessSafetyRulesAsync(
        CancellationToken cancellationToken = default);

    Task<ComprehensiveSelfTestResult> RunFullDiagnosticSelfTestAsync(
        CancellationToken cancellationToken = default);
}
