namespace PerformanceScheduler.Core.Profiles.Validation;

public enum ProfileSafetySeverity
{
    Warning,
    Error
}

public sealed record ProfileSafetyIssue(
    string Code,
    ProfileSafetySeverity Severity,
    string Message,
    ProfileSafetyRiskLevel RiskLevel = ProfileSafetyRiskLevel.Notice);

public sealed record ProfileSafetyValidationResult
{
    public IReadOnlyList<ProfileSafetyIssue> Issues { get; init; } = Array.Empty<ProfileSafetyIssue>();

    public IReadOnlyList<ProfileSafetyIssue> Errors =>
        Issues.Where(issue => issue.Severity == ProfileSafetySeverity.Error).ToArray();

    public IReadOnlyList<ProfileSafetyIssue> Warnings =>
        Issues.Where(issue => issue.Severity == ProfileSafetySeverity.Warning).ToArray();

    public ProfileSafetyRiskLevel HighestRiskLevel =>
        Issues.Count == 0
            ? ProfileSafetyRiskLevel.Safe
            : Issues.Max(issue => issue.RiskLevel);

    public bool CanPersist => Errors.Count == 0;
}
