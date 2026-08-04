namespace PerformanceScheduler.Core.Models;

public enum SchedulerActionReasonCode
{
    None,
    SafeRollbackUnavailable,
    CapabilityUnavailable,
    BaselineCaptureFailed,
    TargetAlreadyActive,
    TargetNotActive,
    SafetyPolicyBlocked,
    ProviderApplyFailed,
    ProcessStateUnavailable,
    ProviderResultMissing
}
