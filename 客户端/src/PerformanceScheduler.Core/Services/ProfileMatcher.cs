using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class ProfileMatcher
{
    public ProfileMatchResult? Match(FocusedAppContext app, IEnumerable<PerformanceProfile> profiles)
    {
        ProfileMatchResult? bestMatch = null;
        bool bestMatchIsExactPowerSource = false;
        PerformanceProfile? exactGlobalDefaultProfile = null;
        PerformanceProfile? anyGlobalDefaultProfile = null;

        foreach (var profile in profiles.Where(static candidate => candidate.IsEnabled))
        {
            if (profile.IsGlobalDefault)
            {
                if (profile.PowerSourceMode == app.PowerSourceMode)
                {
                    if (exactGlobalDefaultProfile is null ||
                        profile.Version > exactGlobalDefaultProfile.Version ||
                        (profile.Version == exactGlobalDefaultProfile.Version && string.CompareOrdinal(profile.Id, exactGlobalDefaultProfile.Id) < 0))
                    {
                        exactGlobalDefaultProfile = profile;
                    }
                }
                else if (profile.PowerSourceMode == PowerSourceMode.Any || MatchesPowerSource(app, profile))
                {
                    if (anyGlobalDefaultProfile is null ||
                        profile.Version > anyGlobalDefaultProfile.Version ||
                        (profile.Version == anyGlobalDefaultProfile.Version && string.CompareOrdinal(profile.Id, anyGlobalDefaultProfile.Id) < 0))
                    {
                        anyGlobalDefaultProfile = profile;
                    }
                }

                continue;
            }

            var reasons = ScoreProfile(app, profile);
            var score = reasons.Sum(reason => reason.ScoreContribution);
            if (score <= 0)
            {
                continue;
            }

            bool isExactPowerSource = profile.PowerSourceMode == app.PowerSourceMode &&
                                      app.PowerSourceMode != PowerSourceMode.Any;

            // Strict Priority:
            // 1. Exact PowerSourceMode match always beats PowerSourceMode.Any match regardless of score
            // 2. If both have the same power source specificity, higher score wins
            bool isBetterMatch = bestMatch is null;
            if (bestMatch is not null)
            {
                if (isExactPowerSource && !bestMatchIsExactPowerSource)
                {
                    isBetterMatch = true;
                }
                else if (!isExactPowerSource && bestMatchIsExactPowerSource)
                {
                    isBetterMatch = false;
                }
                else
                {
                    if (score != bestMatch.Score)
                    {
                        isBetterMatch = score > bestMatch.Score;
                    }
                    else
                    {
                        // Deterministic tie-breaking:
                        // 1. Higher Version wins
                        // 2. Lexicographically smaller Profile.Id wins
                        if (profile.Version != bestMatch.Profile.Version)
                        {
                            isBetterMatch = profile.Version > bestMatch.Profile.Version;
                        }
                        else
                        {
                            isBetterMatch = string.CompareOrdinal(profile.Id, bestMatch.Profile.Id) < 0;
                        }
                    }
                }
            }

            if (isBetterMatch)
            {
                bestMatch = new ProfileMatchResult(
                    profile,
                    score,
                    BuildLegacyReason(reasons),
                    reasons);
                bestMatchIsExactPowerSource = isExactPowerSource;
            }
        }

        if (bestMatch is not null)
        {
            return bestMatch;
        }

        var globalDefaultProfile = exactGlobalDefaultProfile ?? anyGlobalDefaultProfile;
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
        var match = profile.Match ?? new ProfileMatchCriteria();
        bool hasAppMatch = false;

        var executableRule = (match.ExecutableNames ?? Array.Empty<string>()).FirstOrDefault(
            name => EqualsIgnoreCase(name, executableName));
        if (executableRule is not null)
        {
            hasAppMatch = true;
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.ExecutableName,
                100,
                executableRule,
                executableName));
        }

        var processRule = (match.ProcessNames ?? Array.Empty<string>()).FirstOrDefault(
            name => EqualsIgnoreCase(name, app.ProcessName));
        if (processRule is not null)
        {
            hasAppMatch = true;
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.ProcessName,
                80,
                processRule,
                app.ProcessName));
        }

        var titleRule = (match.WindowTitleContains ?? Array.Empty<string>()).FirstOrDefault(
            fragment => ContainsIgnoreCase(app.WindowTitle, fragment));
        if (titleRule is not null)
        {
            hasAppMatch = true;
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.WindowTitle,
                40,
                titleRule,
                app.WindowTitle));
        }

        if ((match.Classifications ?? Array.Empty<ProcessClassification>()).Contains(app.Classification))
        {
            hasAppMatch = true;
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.Classification,
                30,
                app.Classification.ToString(),
                app.Classification.ToString()));
        }

        if (profile.TargetClassification != ProcessClassification.Unknown &&
            profile.TargetClassification == app.Classification)
        {
            hasAppMatch = true;
            reasons.Add(CreateReason(
                ProfileMatchReasonKind.TargetClassification,
                20,
                profile.TargetClassification.ToString(),
                app.Classification.ToString()));
        }

        if (!hasAppMatch)
        {
            return Array.Empty<ProfileMatchReason>();
        }

        if (profile.PowerSourceMode != PowerSourceMode.Any)
        {
            reasons.Insert(0, CreateReason(
                ProfileMatchReasonKind.PowerSource,
                15,
                profile.PowerSourceMode.ToString(),
                app.PowerSourceMode.ToString()));
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
