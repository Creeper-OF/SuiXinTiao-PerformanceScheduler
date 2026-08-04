using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.Services.Diagnostics;

public sealed record DiagnosticPackageManifest
{
    public int SchemaVersion { get; init; } = 1;

    public DateTimeOffset CreatedAt { get; init; }

    public string ApplicationVersion { get; init; } = string.Empty;

    public string OperatingSystem { get; init; } = string.Empty;

    public string DotNetRuntime { get; init; } = string.Empty;

    public string ProcessArchitecture { get; init; } = string.Empty;

    public string PrivacyMode { get; init; } = "default-redacted";

    public IReadOnlyList<string> IncludedFiles { get; init; } = Array.Empty<string>();
}

public sealed record DiagnosticPackageCollectionError
{
    public string Component { get; init; } = string.Empty;

    public string ErrorType { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed record DiagnosticRunSnapshot
{
    public Guid? RunId { get; init; }

    public Guid? TransactionId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = "<redacted>";

    public string ProfileId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public string PowerPlanName { get; init; } = string.Empty;

    public string Priority { get; init; } = string.Empty;

    public bool Success { get; init; }

    public bool IsRetryDeferred { get; init; }

    public int ConsecutiveFailureCount { get; init; }

    public DateTimeOffset? RetryAfter { get; init; }

    public bool? FailureRecoveryCompleted { get; init; }

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<DiagnosticActionSnapshot> Actions { get; init; } =
        Array.Empty<DiagnosticActionSnapshot>();
}

public sealed record DiagnosticActionSnapshot
{
    public Guid ActionId { get; init; }

    public SchedulerActionKind Kind { get; init; }

    public SchedulerActionStatus Status { get; init; }

    public SchedulerActionReasonCode ReasonCode { get; init; }

    public string Target { get; init; } = string.Empty;

    public string OriginalValue { get; init; } = string.Empty;

    public string TargetValue { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool RequiresRollback { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}

public sealed record DiagnosticLogSnapshot
{
    public DateTimeOffset Timestamp { get; init; }

    public AppLogLevel Level { get; init; }

    public LogAudience Audience { get; init; }

    public UserEventCode EventCode { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
}

public sealed record DiagnosticSafetySettingsSnapshot
{
    public bool PauseSchedulingAfterUnsafeExit { get; init; }

    public bool AutoRollbackOnUnsafeExit { get; init; }

    public bool DisableProfileAfterRepeatedUnsafeRuns { get; init; }

    public int UnsafeRunThreshold { get; init; }

    public int MonitoringIntervalSeconds { get; init; }

    public bool AutoStartMonitoringOnLaunch { get; init; }

    public bool LaunchAtStartup { get; init; }
}
