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
}
