namespace PerformanceScheduler.Core.Models;

public enum ConditionalProcessUpdateResult
{
    Applied,
    IdentityMismatch,
    CurrentValueMismatch,
    Failed
}
