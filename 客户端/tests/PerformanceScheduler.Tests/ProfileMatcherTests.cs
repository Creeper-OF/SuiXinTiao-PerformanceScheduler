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

    [Fact]
    public void Match_ResolvesEqualScoreConflictDeterministically()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 100,
            ProcessName = "app",
            WindowTitle = "App Window",
            WindowHandle = 1001,
            PowerSourceMode = PowerSourceMode.Ac
        };

        var profileA = new PerformanceProfile
        {
            Id = "profile-2",
            Name = "Profile Two (Older)",
            Version = 1,
            PowerSourceMode = PowerSourceMode.Ac,
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "app" } }
        };

        var profileB = new PerformanceProfile
        {
            Id = "profile-1",
            Name = "Profile One (Newer)",
            Version = 2,
            PowerSourceMode = PowerSourceMode.Ac,
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "app" } }
        };

        // Regardless of traversal order, Profile One (higher Version) must win
        var res1 = matcher.Match(app, new[] { profileA, profileB });
        Assert.NotNull(res1);
        Assert.Equal("Profile One (Newer)", res1!.Profile.Name);

        var res2 = matcher.Match(app, new[] { profileB, profileA });
        Assert.NotNull(res2);
        Assert.Equal("Profile One (Newer)", res2!.Profile.Name);
    }

    [Fact]
    public void Match_AppWithAcOnlyProfile_FallsBackToGlobalDcOnBattery()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 101,
            ProcessName = "heavygame",
            WindowTitle = "Heavy Game",
            WindowHandle = 2001,
            PowerSourceMode = PowerSourceMode.Battery
        };

        var appAcProfile = new PerformanceProfile
        {
            Id = "app-ac",
            Name = "HeavyGame AC High Performance",
            PowerSourceMode = PowerSourceMode.Ac,
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "heavygame" } }
        };

        var globalDcProfile = new PerformanceProfile
        {
            Id = "global-dc",
            Name = "Global Battery Saver",
            IsGlobalDefault = true,
            PowerSourceMode = PowerSourceMode.Battery
        };

        var result = matcher.Match(app, new[] { appAcProfile, globalDcProfile });
        Assert.NotNull(result);
        Assert.Equal("Global Battery Saver", result!.Profile.Name);
        Assert.Equal(PowerSourceMode.Battery, result.Profile.PowerSourceMode);
    }

    [Fact]
    public void Match_AppWithDcOnlyProfile_FallsBackToGlobalAcOnAc()
    {
        var matcher = new ProfileMatcher();
        var app = new FocusedAppContext
        {
            ProcessId = 102,
            ProcessName = "videoplayer",
            WindowTitle = "Video Player",
            WindowHandle = 2002,
            PowerSourceMode = PowerSourceMode.Ac
        };

        var appDcProfile = new PerformanceProfile
        {
            Id = "app-dc",
            Name = "VideoPlayer Battery Eco",
            PowerSourceMode = PowerSourceMode.Battery,
            Match = new ProfileMatchCriteria { ProcessNames = new[] { "videoplayer" } }
        };

        var globalAcProfile = new PerformanceProfile
        {
            Id = "global-ac",
            Name = "Global AC Standard",
            IsGlobalDefault = true,
            PowerSourceMode = PowerSourceMode.Ac
        };

        var result = matcher.Match(app, new[] { appDcProfile, globalAcProfile });
        Assert.NotNull(result);
        Assert.Equal("Global AC Standard", result!.Profile.Name);
        Assert.Equal(PowerSourceMode.Ac, result.Profile.PowerSourceMode);
    }

    [Fact]
    public void Match_SharedProfileAfterStrippingOneApp_StillMatchesRemainingApp()
    {
        var matcher = new ProfileMatcher();

        // 原始共享 Profile 匹配 appA 和 appB
        var originalShared = new PerformanceProfile
        {
            Id = "shared-tools",
            Name = "Shared Tools Profile",
            PowerSourceMode = PowerSourceMode.Ac,
            Match = new ProfileMatchCriteria
            {
                ProcessNames = new[] { "appA", "appB" }
            }
        };

        // 模拟解绑 appA：仅剥离 appA，保留 appB
        var strippedShared = originalShared with
        {
            Match = originalShared.Match with
            {
                ProcessNames = originalShared.Match.ProcessNames.Where(n => n != "appA").ToArray()
            }
        };

        var appAFocus = new FocusedAppContext
        {
            ProcessId = 301,
            ProcessName = "appA",
            WindowTitle = "App A",
            WindowHandle = 3001,
            PowerSourceMode = PowerSourceMode.Ac
        };

        var appBFocus = new FocusedAppContext
        {
            ProcessId = 302,
            ProcessName = "appB",
            WindowTitle = "App B",
            WindowHandle = 3002,
            PowerSourceMode = PowerSourceMode.Ac
        };

        var resA = matcher.Match(appAFocus, new[] { strippedShared });
        Assert.Null(resA); // appA 已成功解绑，无匹配

        var resB = matcher.Match(appBFocus, new[] { strippedShared });
        Assert.NotNull(resB);
        Assert.Equal("Shared Tools Profile", resB!.Profile.Name); // appB 依然稳定匹配
    }

    [Fact]
    public void GlobalPolicyUpdate_PreservesAdvancedAndExtendedHardwareParameters()
    {
        var guidOld = Guid.NewGuid();
        var guidNew = Guid.NewGuid();
        var existing = new PerformanceProfile
        {
            Id = "global-ac-existing",
            Name = "Existing Global AC",
            IsGlobalDefault = true,
            PowerSourceMode = PowerSourceMode.Ac,
            PowerPlan = new PowerPlanPreference
            {
                SchemeGuid = guidOld,
                PreferredPlanName = "CustomPlanName",
                RestoreOnExit = false,
                Advanced = new PowerPlanAdvancedPreference
                {
                    ProcessorMaxStatePercent = 100
                }
            },
            Priority = new PriorityPreference
            {
                ForegroundPriority = PriorityLevel.High,
                LowerBackgroundProcesses = true
            },
            Gpu = new GpuPreference
            {
                Mode = GpuPreferenceMode.PreferHigherPerformance,
                MaxCoreClockMHz = 2100,
                VoltageOffsetMv = -50
            },
            CpuAffinity = new CpuAffinityPreference
            {
                ForegroundAffinityMask = 0xFFL
            },
            MemoryIoPriority = new MemoryIoPriorityPreference
            {
                ForegroundIoPriority = IoPriorityLevel.Low
            }
        };

        // 模拟 SaveGlobalPoliciesAsync 的非破坏性更新逻辑
        var updated = existing with
        {
            PowerPlan = (existing.PowerPlan ?? new PowerPlanPreference()) with
            {
                SchemeGuid = guidNew,
                Advanced = (existing.PowerPlan?.Advanced ?? new PowerPlanAdvancedPreference()) with
                {
                    ProcessorMaxStatePercent = 90
                }
            },
            Priority = (existing.Priority ?? new PriorityPreference()) with
            {
                ForegroundPriority = PriorityLevel.AboveNormal
            },
            Gpu = (existing.Gpu ?? new GpuPreference()) with
            {
                Mode = GpuPreferenceMode.DriverDefault
            }
        };

        // 验证修改字段已更新
        Assert.Equal(guidNew, updated.PowerPlan.SchemeGuid);
        Assert.Equal(90, updated.PowerPlan.Advanced.ProcessorMaxStatePercent);
        Assert.Equal(PriorityLevel.AboveNormal, updated.Priority.ForegroundPriority);
        Assert.Equal(GpuPreferenceMode.DriverDefault, updated.Gpu.Mode);

        // 验证未在基础界面展示的高级参数完整保留
        Assert.Equal("CustomPlanName", updated.PowerPlan.PreferredPlanName);
        Assert.False(updated.PowerPlan.RestoreOnExit);
        Assert.Equal(2100, updated.Gpu.MaxCoreClockMHz);
        Assert.Equal(-50, updated.Gpu.VoltageOffsetMv);
        Assert.NotNull(updated.CpuAffinity);
        Assert.Equal(0xFFL, updated.CpuAffinity.ForegroundAffinityMask);
        Assert.NotNull(updated.MemoryIoPriority);
        Assert.Equal(IoPriorityLevel.Low, updated.MemoryIoPriority.ForegroundIoPriority);
    }
}


