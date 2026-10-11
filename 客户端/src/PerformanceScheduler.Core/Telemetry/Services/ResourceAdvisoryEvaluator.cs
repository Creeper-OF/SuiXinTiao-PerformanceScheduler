using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Core.Telemetry.Services;

/// <summary>
/// Turns telemetry evidence into a conservative user-facing suggestion.
/// It deliberately has no scheduling or hardware-control dependency.
/// </summary>
public sealed class ResourceAdvisoryEvaluator
{
    public ResourceAdvisorySummary Evaluate(
        ResourcePressureSummary pressure,
        ResourceContentionSummary contention)
    {
        ArgumentNullException.ThrowIfNull(pressure);
        ArgumentNullException.ThrowIfNull(contention);

        var kind = !pressure.HasData && !contention.HasComparison
            ? ResourceAdvisoryKind.Unknown
            : contention.Level is ResourceContentionLevel.High or ResourceContentionLevel.Elevated
                ? ResourceAdvisoryKind.ReviewBackgroundLoad
                : pressure.Level == ResourcePressureLevel.High &&
                  contention.Level == ResourceContentionLevel.None
                    ? ResourceAdvisoryKind.ForegroundDominatesLoad
                    : ResourceAdvisoryKind.NoImmediateAction;

        return new ResourceAdvisorySummary
        {
            Kind = kind,
            PressureLevel = pressure.Level,
            ContentionLevel = contention.Level
        };
    }
}
