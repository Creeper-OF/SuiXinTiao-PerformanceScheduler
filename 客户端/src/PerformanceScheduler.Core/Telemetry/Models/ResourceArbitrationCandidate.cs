using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Telemetry.Models;

/// <summary>
/// A measured background candidate. It is intentionally separate from a
/// process write request so diagnostics cannot accidentally become an action.
/// </summary>
public sealed record ResourceArbitrationCandidate
{
    public required int ProcessId { get; init; }

    public required string ProcessName { get; init; }

    /// <summary>
    /// Process start time is part of the identity so a reused PID cannot inherit
    /// an observation from a previous process instance.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    public ProcessClassification Classification { get; init; } = ProcessClassification.Unknown;

    public bool IsProtected { get; init; }

    public bool HasApplicablePolicy { get; init; }

    public double? CpuUsagePercent { get; init; }

    public double? GpuUsagePercent { get; init; }
}
