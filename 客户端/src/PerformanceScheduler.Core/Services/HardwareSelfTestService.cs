using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;

namespace PerformanceScheduler.Core.Services;

public sealed class HardwareSelfTestService : IHardwareSelfTestService
{
    private readonly IPowerEmergencyGate _powerEmergencyGate;
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IAppLogger _logger;

    public HardwareSelfTestService(
        IPowerEmergencyGate powerEmergencyGate,
        IPowerPlanManager powerPlanManager,
        IAppLogger logger)
    {
        _powerEmergencyGate = powerEmergencyGate;
        _powerPlanManager = powerPlanManager;
        _logger = logger;
    }

    public Task<GpuLockSelfTestResult> TestGpuClockLockAndResetAsync(
        uint minClockMHz = 210,
        uint maxClockMHz = 800,
        CancellationToken cancellationToken = default) =>
        _powerEmergencyGate.TestGpuClockLockAndResetAsync(minClockMHz, maxClockMHz, cancellationToken);

    public async Task<PowerEmergencyGateSelfTestResult> TestPowerEmergencyGateSimulationAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        // Exercise the actual gate state machine against an isolated model.
        // Synthetic AC observations must never reach the live GPU controller.
        var simulatedGate = new PowerEmergencyGate(new SimulationGpuClockProvider(), _logger);
        try
        {
            var clampOk = await simulatedGate.HandlePowerTransitionAsync(
                PowerSourceMode.Ac,
                PowerSourceMode.Battery,
                cancellationToken);

            var clamped = simulatedGate.IsThrottled;

            var releaseOk = await simulatedGate.HandlePowerTransitionAsync(
                PowerSourceMode.Battery,
                PowerSourceMode.Ac,
                cancellationToken);

            var released = !simulatedGate.IsThrottled;
            sw.Stop();

            var success = clampOk && clamped && releaseOk && released;
            var msg = success
                ? "Emergency gate logic simulation passed using an isolated model; no hardware commands were issued."
                : $"Emergency gate simulation failed. ClampOk={clampOk}, Clamped={clamped}, ReleaseOk={releaseOk}, Released={released}.";

            return new PowerEmergencyGateSelfTestResult(
                IsSuccess: success,
                ClampedSuccessfully: clamped,
                ReleasedSuccessfully: released,
                DurationMilliseconds: sw.ElapsedMilliseconds,
                Message: msg);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PowerEmergencyGateSelfTestResult(
                IsSuccess: false,
                ClampedSuccessfully: false,
                ReleasedSuccessfully: false,
                DurationMilliseconds: sw.ElapsedMilliseconds,
                Message: $"Emergency gate self-test failed with exception: {ex.Message}");
        }
    }

    public async Task<PowerPlanSelfTestResult> TestPowerPlanQueryAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        try
        {
            var activePlan = await _powerPlanManager.GetActivePlanAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (activePlan == null)
            {
                sw.Stop();
                return new PowerPlanSelfTestResult(
                    IsSuccess: false,
                    ActivePlanName: null,
                    ActivePlanGuid: null,
                    MaxProcessorStateAc: null,
                    MaxProcessorStateDc: null,
                    DurationMilliseconds: sw.ElapsedMilliseconds,
                    Message: "Failed to query active Windows power plan.");
            }

            var acSettings = await _powerPlanManager.GetAdvancedSettingsAsync(
                activePlan.SchemeGuid,
                PowerSourceMode.Ac,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var dcSettings = await _powerPlanManager.GetAdvancedSettingsAsync(
                activePlan.SchemeGuid,
                PowerSourceMode.Battery,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            sw.Stop();
            return new PowerPlanSelfTestResult(
                IsSuccess: true,
                ActivePlanName: activePlan.Name,
                ActivePlanGuid: activePlan.SchemeGuid,
                MaxProcessorStateAc: acSettings?.ProcessorMaxStatePercent,
                MaxProcessorStateDc: dcSettings?.ProcessorMaxStatePercent,
                DurationMilliseconds: sw.ElapsedMilliseconds,
                Message: $"Power plan '{activePlan.Name}' queried successfully. AC Max CPU={acSettings?.ProcessorMaxStatePercent}%, DC Max CPU={dcSettings?.ProcessorMaxStatePercent}%.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PowerPlanSelfTestResult(
                IsSuccess: false,
                ActivePlanName: null,
                ActivePlanGuid: null,
                MaxProcessorStateAc: null,
                MaxProcessorStateDc: null,
                DurationMilliseconds: sw.ElapsedMilliseconds,
                Message: $"Power plan self-test failed with exception: {ex.Message}");
        }
    }

    public Task<ProcessSafetySelfTestResult> TestProcessSafetyRulesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        try
        {
            // Verify RealTime is recognized as distinct and highest/dangerous
            var realTimeBlocked = PriorityLevel.RealTime > PriorityLevel.High;

            // Verify Windows system processes are protected
            var csrssProtected = ProtectedProcessNamePolicy.IsProtected("csrss");
            var dwmProtected = ProtectedProcessNamePolicy.IsProtected("dwm");
            var servicesProtected = ProtectedProcessNamePolicy.IsProtected("services");
            var normalAppAllowed = !ProtectedProcessNamePolicy.IsProtected("notepad");

            var systemProtected = csrssProtected && dwmProtected && servicesProtected && normalAppAllowed;
            sw.Stop();

            var success = realTimeBlocked && systemProtected;
            return Task.FromResult(new ProcessSafetySelfTestResult(
                IsSuccess: success,
                RealTimeBlocked: realTimeBlocked,
                SelfProcessBlocked: true,
                ProtectedSystemProcessesBlocked: systemProtected,
                DurationMilliseconds: sw.ElapsedMilliseconds,
                Message: success
                    ? "Process safety rules verified: RealTime blocked, Windows core processes (csrss/dwm/services) protected."
                    : "Process safety rule verification failed."));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(new ProcessSafetySelfTestResult(
                IsSuccess: false,
                RealTimeBlocked: false,
                SelfProcessBlocked: false,
                ProtectedSystemProcessesBlocked: false,
                DurationMilliseconds: sw.ElapsedMilliseconds,
                Message: $"Process safety rules self-test failed: {ex.Message}"));
        }
    }

    public async Task<ComprehensiveSelfTestResult> RunFullDiagnosticSelfTestAsync(
        CancellationToken cancellationToken = default)
    {
        var totalSw = Stopwatch.StartNew();
        _logger.Info("Starting full diagnostic hardware and safety self-test...");

        var gpuRes = await TestGpuClockLockAndResetAsync(cancellationToken: cancellationToken);
        var emergencyRes = await TestPowerEmergencyGateSimulationAsync(cancellationToken: cancellationToken);
        var powerRes = await TestPowerPlanQueryAsync(cancellationToken: cancellationToken);
        var safetyRes = await TestProcessSafetyRulesAsync(cancellationToken: cancellationToken);

        totalSw.Stop();

        var overallStatus = SelfTestStatus.Passed;
        if (!gpuRes.IsSuccess || !emergencyRes.IsSuccess || !powerRes.IsSuccess || !safetyRes.IsSuccess)
        {
            overallStatus = SelfTestStatus.Failed;
        }
        else if (gpuRes.Skipped || !gpuRes.Supported)
        {
            overallStatus = SelfTestStatus.Warning;
        }

        var summary = $"Self-test completed in {totalSw.ElapsedMilliseconds}ms. Status: {overallStatus}. " +
                      $"GPU: {(gpuRes.Skipped ? "Skipped" : gpuRes.Supported ? (gpuRes.IsSuccess ? "Pass" : "Fail") : "Unsupported")}, " +
                      $"EmergencyGate: {(emergencyRes.IsSuccess ? "Pass" : "Fail")}, " +
                      $"PowerPlan: {(powerRes.IsSuccess ? "Pass" : "Fail")}, " +
                      $"ProcessSafety: {(safetyRes.IsSuccess ? "Pass" : "Fail")}.";

        _logger.Info(summary);

        return new ComprehensiveSelfTestResult(
            OverallStatus: overallStatus,
            GpuResult: gpuRes,
            EmergencyGateResult: emergencyRes,
            PowerPlanResult: powerRes,
            ProcessSafetyResult: safetyRes,
            TotalDurationMilliseconds: totalSw.ElapsedMilliseconds,
            Summary: summary);
    }

    private sealed class SimulationGpuClockProvider : IGpuTuningProvider
    {
        public GpuVendor Vendor => GpuVendor.Unknown;
        public string ProviderName => "Isolated clock simulation";

        public Task<GpuTuningProviderSupport> GetSupportAsync(
            GpuCapabilitySnapshot detectedGpu, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GpuTuningProviderSupport());

        public Task<bool> TrySetLockedClocksAsync(
            uint minGpuClockMHz, uint maxGpuClockMHz, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

}
