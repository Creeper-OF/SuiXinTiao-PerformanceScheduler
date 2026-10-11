namespace PerformanceScheduler.Core.Models;

public enum SelfTestStatus
{
    Passed,
    Warning,
    Failed,
    Unsupported
}

public sealed record GpuLockSelfTestResult(
    bool IsSuccess,
    string Vendor,
    string ProviderName,
    bool Supported,
    uint TestedMinClockMHz,
    uint TestedMaxClockMHz,
    long DurationMilliseconds,
    string Message)
{
    public bool Skipped { get; init; }
}

public sealed record PowerEmergencyGateSelfTestResult(
    bool IsSuccess,
    bool ClampedSuccessfully,
    bool ReleasedSuccessfully,
    long DurationMilliseconds,
    string Message);

public sealed record PowerPlanSelfTestResult(
    bool IsSuccess,
    string? ActivePlanName,
    Guid? ActivePlanGuid,
    int? MaxProcessorStateAc,
    int? MaxProcessorStateDc,
    long DurationMilliseconds,
    string Message);

public sealed record ProcessSafetySelfTestResult(
    bool IsSuccess,
    bool RealTimeBlocked,
    bool SelfProcessBlocked,
    bool ProtectedSystemProcessesBlocked,
    long DurationMilliseconds,
    string Message);

public sealed record ComprehensiveSelfTestResult(
    SelfTestStatus OverallStatus,
    GpuLockSelfTestResult GpuResult,
    PowerEmergencyGateSelfTestResult EmergencyGateResult,
    PowerPlanSelfTestResult PowerPlanResult,
    ProcessSafetySelfTestResult ProcessSafetyResult,
    long TotalDurationMilliseconds,
    string Summary);
