using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class ProfileMatcherTests
{
    [Fact]
    public void Match_PrefersExecutableNameMatchOverTitleOnlyMatch()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 100,
            ProcessName = "eldenring",
            ExecutablePath = @"C:\Games\EldenRing\eldenring.exe",
            WindowTitle = "ELDEN RING",
            WindowHandle = 123,
            Classification = ProcessClassification.Game
        };

        var profiles = new[]
        {
            new PerformanceProfile
            {
                Name = "Title Match",
                Match = new ProfileMatchCriteria
                {
                    WindowTitleContains = new[] { "ELDEN" }
                }
            },
            new PerformanceProfile
            {
                Name = "Executable Match",
                Match = new ProfileMatchCriteria
                {
                    ExecutableNames = new[] { "eldenring.exe" }
                }
            }
        };

        var result = matcher.Match(app, profiles);

        Assert.NotNull(result);
        Assert.Equal("Executable Match", result!.Profile.Name);
        Assert.Equal(100, result.Score);
        var reason = Assert.Single(result.Reasons);
        Assert.Equal(ProfileMatchReasonKind.ExecutableName, reason.Kind);
        Assert.Equal(100, reason.ScoreContribution);
        Assert.Equal("eldenring.exe", reason.RuleValue);
        Assert.Equal("eldenring.exe", reason.ActualValue);
    }

    [Fact]
    public void Match_ReturnsNullWhenNoProfileMatches()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 42,
            ProcessName = "calc",
            ExecutablePath = @"C:\Windows\System32\calc.exe",
            WindowTitle = "Calculator",
            WindowHandle = 456,
            Classification = ProcessClassification.Productivity
        };

        var profiles = new[]
        {
            new PerformanceProfile
            {
                Name = "Game Profile",
                Match = new ProfileMatchCriteria
                {
                    ExecutableNames = new[] { "game.exe" },
                    Classifications = new[] { ProcessClassification.Game }
                }
            }
        };

        var result = matcher.Match(app, profiles);

        Assert.Null(result);
    }

    [Fact]
    public void Match_UsesGlobalDefaultWhenSpecificProfilesDoNotMatch()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 42,
            ProcessName = "calc",
            ExecutablePath = @"C:\Windows\System32\calc.exe",
            WindowTitle = "Calculator",
            WindowHandle = 456,
            Classification = ProcessClassification.Productivity
        };

        var profiles = new[]
        {
            new PerformanceProfile
            {
                Name = "Game Profile",
                Match = new ProfileMatchCriteria
                {
                    ExecutableNames = new[] { "game.exe" }
                }
            },
            new PerformanceProfile
            {
                Name = "Global Default",
                IsGlobalDefault = true
            }
        };

        var result = matcher.Match(app, profiles);

        Assert.NotNull(result);
        Assert.Equal("Global Default", result!.Profile.Name);
        Assert.Equal("Global default fallback", result.Reason);
        var reason = Assert.Single(result.Reasons);
        Assert.Equal(ProfileMatchReasonKind.GlobalDefaultFallback, reason.Kind);
        Assert.Equal(1, reason.ScoreContribution);
    }

    [Fact]
    public void Match_SkipsAcOnlyProfileWhenRunningOnBattery()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 100,
            ProcessName = "eldenring",
            ExecutablePath = @"C:\Games\EldenRing\eldenring.exe",
            WindowTitle = "ELDEN RING",
            WindowHandle = 123,
            Classification = ProcessClassification.Game,
            PowerSourceMode = PowerSourceMode.Battery
        };

        var profiles = new[]
        {
            new PerformanceProfile
            {
                Name = "Plugged In Game",
                PowerSourceMode = PowerSourceMode.Ac,
                Match = new ProfileMatchCriteria
                {
                    ExecutableNames = new[] { "eldenring.exe" }
                }
            },
            new PerformanceProfile
            {
                Name = "Battery Game",
                PowerSourceMode = PowerSourceMode.Battery,
                Match = new ProfileMatchCriteria
                {
                    ExecutableNames = new[] { "eldenring.exe" }
                }
            }
        };

        var result = matcher.Match(app, profiles);

        Assert.NotNull(result);
        Assert.Equal("Battery Game", result!.Profile.Name);
    }

    [Fact]
    public void Match_SkipsGlobalDefaultWhenPowerSourceDoesNotMatch()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 42,
            ProcessName = "calc",
            ExecutablePath = @"C:\Windows\System32\calc.exe",
            WindowTitle = "Calculator",
            WindowHandle = 456,
            Classification = ProcessClassification.Productivity,
            PowerSourceMode = PowerSourceMode.Battery
        };

        var profiles = new[]
        {
            new PerformanceProfile
            {
                Name = "Plugged In Global Default",
                IsGlobalDefault = true,
                PowerSourceMode = PowerSourceMode.Ac
            }
        };

        var result = matcher.Match(app, profiles);

        Assert.Null(result);
    }

    [Fact]
    public void Match_ReturnsStructuredReasonsThatAddUpToScore()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 100,
            ProcessName = "eldenring",
            ExecutablePath = @"C:\Games\EldenRing\eldenring.exe",
            WindowTitle = "ELDEN RING - Main Window",
            WindowHandle = 123,
            Classification = ProcessClassification.Game,
            PowerSourceMode = PowerSourceMode.Ac
        };
        var profile = new PerformanceProfile
        {
            Name = "Complete Match",
            PowerSourceMode = PowerSourceMode.Ac,
            TargetClassification = ProcessClassification.Game,
            Match = new ProfileMatchCriteria
            {
                ExecutableNames = new[] { "eldenring.exe" },
                ProcessNames = new[] { "eldenring" },
                WindowTitleContains = new[] { "ELDEN RING" },
                Classifications = new[] { ProcessClassification.Game }
            }
        };

        var result = matcher.Match(app, new[] { profile });

        Assert.NotNull(result);
        Assert.Equal(285, result!.Score);
        Assert.Equal(result.Score, result.Reasons.Sum(reason => reason.ScoreContribution));
        Assert.Collection(
            result.Reasons,
            reason => AssertReason(reason, ProfileMatchReasonKind.PowerSource, 15, "Ac", "Ac"),
            reason => AssertReason(reason, ProfileMatchReasonKind.ExecutableName, 100, "eldenring.exe", "eldenring.exe"),
            reason => AssertReason(reason, ProfileMatchReasonKind.ProcessName, 80, "eldenring", "eldenring"),
            reason => AssertReason(reason, ProfileMatchReasonKind.WindowTitle, 40, "ELDEN RING", "ELDEN RING - Main Window"),
            reason => AssertReason(reason, ProfileMatchReasonKind.Classification, 30, "Game", "Game"),
            reason => AssertReason(reason, ProfileMatchReasonKind.TargetClassification, 20, "Game", "Game"));
        Assert.Equal(
            "Power source matched, Executable matched, Process name matched, Window title matched, Classification matched, Target classification matched",
            result.Reason);
    }

    private static void AssertReason(
        ProfileMatchReason reason,
        ProfileMatchReasonKind expectedKind,
        int expectedScore,
        string expectedRule,
        string expectedActual)
    {
        Assert.Equal(expectedKind, reason.Kind);
        Assert.Equal(expectedScore, reason.ScoreContribution);
        Assert.Equal(expectedRule, reason.RuleValue);
        Assert.Equal(expectedActual, reason.ActualValue);
    }

    [Fact]
    public void Match_ExactPowerSourceMatchBeatsAnyMatchEvenIfAnyHasHigherScore()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 100,
            ProcessName = "eldenring",
            ExecutablePath = @"C:\Games\EldenRing\eldenring.exe",
            WindowTitle = "ELDEN RING",
            WindowHandle = 123,
            Classification = ProcessClassification.Game,
            PowerSourceMode = PowerSourceMode.Ac
        };

        // Any profile has Executable (100) + Process (80) + Title (40) = 220 pts
        var anyProfile = new PerformanceProfile
        {
            Name = "Any Game Profile",
            PowerSourceMode = PowerSourceMode.Any,
            Match = new ProfileMatchCriteria
            {
                ExecutableNames = new[] { "eldenring.exe" },
                ProcessNames = new[] { "eldenring" },
                WindowTitleContains = new[] { "ELDEN RING" }
            }
        };

        // Exact AC profile only has ProcessName (80) + PowerSource (15) = 95 pts
        var acProfile = new PerformanceProfile
        {
            Name = "AC Dedicated Profile",
            PowerSourceMode = PowerSourceMode.Ac,
            Match = new ProfileMatchCriteria
            {
                ProcessNames = new[] { "eldenring" }
            }
        };

        var result = matcher.Match(app, new[] { anyProfile, acProfile });

        Assert.NotNull(result);
        Assert.Equal("AC Dedicated Profile", result!.Profile.Name);
    }

    [Fact]
    public void Match_PrefersExactPowerSourceGlobalDefaultOverAnyGlobalDefault()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 42,
            ProcessName = "notepad",
            WindowTitle = "Notepad",
            WindowHandle = 789,
            PowerSourceMode = PowerSourceMode.Battery
        };

        var anyDefault = new PerformanceProfile
        {
            Name = "Global Any Default",
            IsGlobalDefault = true,
            PowerSourceMode = PowerSourceMode.Any
        };

        var dcDefault = new PerformanceProfile
        {
            Name = "Global DC Default",
            IsGlobalDefault = true,
            PowerSourceMode = PowerSourceMode.Battery
        };

        // Regardless of order, DC Default must win
        var result1 = matcher.Match(app, new[] { anyDefault, dcDefault });
        Assert.NotNull(result1);
        Assert.Equal("Global DC Default", result1!.Profile.Name);

        var result2 = matcher.Match(app, new[] { dcDefault, anyDefault });
        Assert.NotNull(result2);
        Assert.Equal("Global DC Default", result2!.Profile.Name);
    }
}

