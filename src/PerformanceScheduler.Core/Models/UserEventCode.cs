namespace PerformanceScheduler.Core.Models;

public enum UserEventCode
{
    None,
    PendingRollbackBlocked,
    ProfileRetryDeferred,
    PreviousSessionRestored,
    PreviousSessionRollbackIncomplete,
    FailedRunRollbackCompleted,
    FailedRunRollbackIncomplete,
    AutomaticRollbackFailed
}
