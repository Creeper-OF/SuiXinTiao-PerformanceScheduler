using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class ProfileMatcher
{
    public ProfileMatchResult? Match(FocusedAppContext app, IEnumerable<PerformanceProfile> profiles)
    {
        ProfileMatchResult? bestMatch = null;
        PerformanceProfile? globalDefaultProfile = null;

        foreach (var profile in profiles.Where(static candidate => candidate.IsEnabled))
        {
            if (profile.IsGlobalDefault)
            {
                if (MatchesPowerSource(app, profile))
                {
                    globalDefaultProfile ??= profile;
                }

                continue;
            }

            var (score, reason) = ScoreProfile(app, profile);
            if (score <= 0)
            {
                continue;
            }

            if (bestMatch is null || score > bestMatch.Score)
            {
                bestMatch = new ProfileMatchResult(profile, score, reason);
            }
        }

        return bestMatch ?? (globalDefaultProfile is null
            ? null
            : new ProfileMatchResult(globalDefaultProfile, 1, "Global default fallback"));
    }

    private static (int Score, string Reason) ScoreProfile(FocusedAppContext app, PerformanceProfile profile)
    {
        if (!MatchesPowerSource(app, profile))
        {
            return (0, string.Empty);
        }

        var score = 0;
        var reasons = new List<string>();
        var executableName = Path.GetFileName(app.ExecutablePath ?? string.Empty);

        if (profile.PowerSourceMode != PowerSourceMode.Any)
        {
            score += 15;
            reasons.Add("Power source matched");
        }

        if (profile.Match.ExecutableNames.Any(name => EqualsIgnoreCase(name, executableName)))
        {
            score += 100;
            reasons.Add("Executable matched");
        }

        if (profile.Match.ProcessNames.Any(name => EqualsIgnoreCase(name, app.ProcessName)))
        {
            score += 80;
            reasons.Add("Process name matched");
        }

        if (profile.Match.WindowTitleContains.Any(fragment => ContainsIgnoreCase(app.WindowTitle, fragment)))
        {
            score += 40;
            reasons.Add("Window title matched");
        }

        if (profile.Match.Classifications.Contains(app.Classification))
        {
            score += 30;
            reasons.Add("Classification matched");
        }

        if (profile.TargetClassification != ProcessClassification.Unknown &&
            profile.TargetClassification == app.Classification)
        {
            score += 20;
            reasons.Add("Target classification matched");
        }

        return (score, string.Join(", ", reasons));
    }

    private static bool EqualsIgnoreCase(string left, string right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(string input, string fragment) =>
        !string.IsNullOrWhiteSpace(fragment) &&
        input.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesPowerSource(FocusedAppContext app, PerformanceProfile profile) =>
        profile.PowerSourceMode == PowerSourceMode.Any ||
        profile.PowerSourceMode == app.PowerSourceMode;
}
