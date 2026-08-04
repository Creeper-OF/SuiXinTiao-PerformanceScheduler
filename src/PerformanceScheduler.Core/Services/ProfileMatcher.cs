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

            var reasons = ScoreProfile(app, profile);
            var score = reasons.Sum(reason => reason.ScoreContribution);
            if (score <= 0)
            {
                continue;
            }

            if (bestMatch is null || score > bestMatch.Score)
            {
                bestMatch = new ProfileMatchResult(
                    profile,
                    score,
                    BuildLegacyReason(reasons),
                    reasons);
            }
        }

        if (bestMatch is not null)
        {
            return bestMatch;
        }

        if (globalDefaultProfile is null)
        {
            return null;
        }

        ProfileMatchReason[] fallbackReasons =
        [
            new ProfileMatchReason
            {
                Kind = ProfileMatchReasonKind.GlobalDefaultFallback,
                ScoreContribution = 1,
                RuleValue = globalDefaultProfile.Name,
                ActualValue = app.ProcessName
            }
        ];
        return new ProfileMatchResult(
            globalDefaultProfile,
            score: 1,
            reason: "Global default fallback",
            reasons: fallbackReasons);
    }

    private static IReadOnlyList<ProfileMatchReason> ScoreProfile(
        FocusedAppContext app,
        PerformanceProfile profile)
    {
        if (!MatchesPowerSource(app, profile))
        {
            return Array.Empty<ProfileMatchReason>();
        }

        var reasons = new List<ProfileMatchReason>();
        var executableName = Path.GetFileName(app.ExecutablePath ?? string.Empty);

        if (profile.PowerSourceMode != PowerSourceMode.Any)
        {
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.PowerSource,
                15,
                profile.PowerSourceMode.ToString(),
                app.PowerSourceMode.ToString()));
        }

        var executableRule = profile.Match.ExecutableNames.FirstOrDefault(
            name => EqualsIgnoreCase(name, executableName));
        if (executableRule is not null)
        {
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.ExecutableName,
                100,
                executableRule,
                executableName));
        }

        var processRule = profile.Match.ProcessNames.FirstOrDefault(
            name => EqualsIgnoreCase(name, app.ProcessName));
        if (processRule is not null)
        {
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.ProcessName,
                80,
                processRule,
                app.ProcessName));
        }

        var titleRule = profile.Match.WindowTitleContains.FirstOrDefault(
            fragment => ContainsIgnoreCase(app.WindowTitle, fragment));
        if (titleRule is not null)
        {
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.WindowTitle,
                40,
                titleRule,
                app.WindowTitle));
        }

        if (profile.Match.Classifications.Contains(app.Classification))
        {
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.Classification,
                30,
                app.Classification.ToString(),
                app.Classification.ToString()));
        }

        if (profile.TargetClassification != ProcessClassification.Unknown &&
            profile.TargetClassification == app.Classification)
        {
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.TargetClassification,
                20,
                profile.TargetClassification.ToString(),
                app.Classification.ToString()));
        }

        return reasons;
    }

    private static ProfileMatchReason CreateReason(
        ProfileMatchReasonKind kind,
        int scoreContribution,
        string ruleValue,
        string actualValue) =>
        new()
        {
            Kind = kind,
            ScoreContribution = scoreContribution,
            RuleValue = ruleValue,
            ActualValue = actualValue
        };

    private static string BuildLegacyReason(IEnumerable<ProfileMatchReason> reasons) =>
        string.Join(", ", reasons.Select(reason => reason.Kind switch
        {
            ProfileMatchReasonKind.PowerSource => "Power source matched",
            ProfileMatchReasonKind.ExecutableName => "Executable matched",
            ProfileMatchReasonKind.ProcessName => "Process name matched",
            ProfileMatchReasonKind.WindowTitle => "Window title matched",
            ProfileMatchReasonKind.Classification => "Classification matched",
            ProfileMatchReasonKind.TargetClassification => "Target classification matched",
            ProfileMatchReasonKind.GlobalDefaultFallback => "Global default fallback",
            _ => reason.Kind.ToString()
        }));

    private static bool EqualsIgnoreCase(string left, string right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(string input, string fragment) =>
        !string.IsNullOrWhiteSpace(fragment) &&
        input.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesPowerSource(FocusedAppContext app, PerformanceProfile profile) =>
        profile.PowerSourceMode == PowerSourceMode.Any ||
        profile.PowerSourceMode == app.PowerSourceMode;
}
