namespace PerformanceScheduler.Core.Profiles.Validation;

public enum ProfileSafetySeverity
{
    Warning,
    Error
}

public sealed record ProfileSafetyIssue(
    string Code,
    ProfileSafetySeverity Severity,
    string Message);

public sealed record ProfileSafetyValidationResult
{
    public IReadOnlyList<ProfileSafetyIssue> Issues { get; init; } = Array.Empty<ProfileSafetyIssue>();

    public IReadOnlyList<ProfileSafetyIssue> Errors =>
        Issues.Where(issue => issue.Severity == ProfileSafetySeverity.Error).ToArray();

    public IReadOnlyList<ProfileSafetyIssue> Warnings =>
        Issues.Where(issue => issue.Severity == ProfileSafetySeverity.Warning).ToArray();

    public bool CanPersist => Errors.Count == 0;
}
