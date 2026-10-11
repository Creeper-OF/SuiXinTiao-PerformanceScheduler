namespace PerformanceScheduler.Core.Profiles.Validation;

public sealed class InvalidProfileConfigurationException : FormatException
{
    public InvalidProfileConfigurationException(
        string profileName,
        IReadOnlyList<ProfileSafetyIssue> issues,
        string? source = null)
        : base(BuildMessage(profileName, issues, source))
    {
        ProfileName = profileName;
        Issues = issues;
        SourcePath = source;
    }

    public string ProfileName { get; }

    public IReadOnlyList<ProfileSafetyIssue> Issues { get; }

    public string? SourcePath { get; }

    private static string BuildMessage(
        string profileName,
        IReadOnlyList<ProfileSafetyIssue> issues,
        string? source)
    {
        var location = string.IsNullOrWhiteSpace(source) ? string.Empty : $" from '{source}'";
        var details = string.Join("; ", issues.Select(issue => issue.Message));
        return $"Profile '{profileName}'{location} failed safety validation: {details}";
    }
}
