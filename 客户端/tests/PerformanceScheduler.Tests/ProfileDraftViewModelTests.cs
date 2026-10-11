using PerformanceScheduler.App.ViewModels;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Tests;

public sealed class ProfileDraftViewModelTests
{
    [Fact]
    public void ToProfile_MapsBackgroundPoliciesIntoProfileModel()
    {
        var draft = new ProfileDraftViewModel
        {
            Name = "Game Draft",
            ExecutableNamesText = "game.exe",
            LowerBackgroundProcesses = true,
            PreferEfficiencyModeForBackground = true
        };

        draft.BackgroundPolicies.Add(new BackgroundPolicyDraftItemViewModel
        {
            Category = "browser",
            TargetPriority = PriorityLevel.Idle,
            PreferEfficiencyMode = true,
            Notes = "Yield to the game."
        });

        var profile = draft.ToProfile();

        var policy = Assert.Single(profile.BackgroundPolicies);
        Assert.Equal("browser", policy.Category);
        Assert.Equal(PriorityLevel.Idle, policy.TargetPriority);
        Assert.True(policy.PreferEfficiencyMode);
        Assert.True(profile.Priority.LowerBackgroundProcesses);
        Assert.True(profile.Priority.PreferEfficiencyModeForBackground);
    }

    [Fact]
    public void LoadAndSave_PreservesBackgroundPriorityPreferences()
    {
        var source = new PerformanceProfile
        {
            Priority = new PriorityPreference
            {
                ForegroundPriority = PriorityLevel.High,
                LowerBackgroundProcesses = true,
                PreferEfficiencyModeForBackground = true
            }
        };
        var draft = new ProfileDraftViewModel();

        draft.LoadFromProfile(source, Array.Empty<PowerPlanOptionViewModel>());
        var roundTripped = draft.ToProfile();

        Assert.Equal(source.Priority, roundTripped.Priority);
    }

    [Fact]
    public void LoadAndSave_PreservesResourceArbitrationPreference()
    {
        var source = new PerformanceProfile
        {
            Priority = new PriorityPreference
            {
                ForegroundPriority = PriorityLevel.AboveNormal,
                LowerBackgroundProcesses = true,
                PreferEfficiencyModeForBackground = true,
                UseResourceArbitration = true
            }
        };
        var draft = new ProfileDraftViewModel();

        draft.LoadFromProfile(source, Array.Empty<PowerPlanOptionViewModel>());
        Assert.True(draft.UseResourceArbitration);

        var roundTripped = draft.ToProfile();
        Assert.True(roundTripped.Priority.UseResourceArbitration);
        Assert.Equal(source.Priority, roundTripped.Priority);

        draft.Reset(Array.Empty<PowerPlanOptionViewModel>());
        Assert.False(draft.UseResourceArbitration);
    }

    [Fact]
    public void Validate_ReturnsIssueWhenBackgroundPolicyCategoriesRepeat()
    {
        var draft = new ProfileDraftViewModel
        {
            Name = "Game Draft",
            ExecutableNamesText = "game.exe"
        };

        draft.BackgroundPolicies.Add(new BackgroundPolicyDraftItemViewModel { Category = "browser" });
        draft.BackgroundPolicies.Add(new BackgroundPolicyDraftItemViewModel { Category = "browser" });

        var issues = draft.Validate(static key => key, static (key, args) => $"{key}:{string.Join(",", args)}");

        Assert.Contains(issues, issue => issue.Contains("Validation.BackgroundPolicyDuplicateCategories", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsGlobalDefaultProfileWithoutMatchRules()
    {
        var draft = new ProfileDraftViewModel
        {
            Name = "Global Default",
            IsGlobalDefault = true
        };

        var issues = draft.Validate(static key => key, static (key, args) => $"{key}:{string.Join(",", args)}");

        Assert.DoesNotContain(issues, issue => issue.Contains("Validation.AtLeastOneMatchRule", StringComparison.Ordinal));
    }

    [Fact]
    public void ToProfile_MapsGpuPreferencesIntoProfileModel()
    {
        var draft = new ProfileDraftViewModel
        {
            Name = "GPU Draft",
            ExecutableNamesText = "game.exe",
            GpuMode = GpuPreferenceMode.VendorExtension,
            GpuMaxCoreClockMHzText = "1800",
            GpuVoltageOffsetMvText = "-75",
            GpuAllowVendorApi = true
        };

        var profile = draft.ToProfile();

        Assert.Equal(GpuPreferenceMode.VendorExtension, profile.Gpu.Mode);
        Assert.Equal(1800, profile.Gpu.MaxCoreClockMHz);
        Assert.Equal(-75, profile.Gpu.VoltageOffsetMv);
        Assert.True(profile.Gpu.AllowVendorApi);
    }

    [Fact]
    public void ToProfile_MapsAdvancedPowerPreferencesIntoProfileModel()
    {
        var draft = new ProfileDraftViewModel
        {
            Name = "Battery Cool Draft",
            ExecutableNamesText = "game.exe",
            ProcessorMaxStatePercentText = "80"
        };

        var profile = draft.ToProfile();

        Assert.Equal(80, profile.PowerPlan.Advanced.ProcessorMaxStatePercent);
    }

    [Fact]
    public void ToProfile_MapsTargetExperiencePreferences()
    {
        var draft = new ProfileDraftViewModel
        {
            Name = "Stable Game Draft",
            ExecutableNamesText = "game.exe",
            TargetExperienceEnabled = true,
            TargetFramesPerSecondText = "60",
            TargetFrameTimeP95MillisecondsText = "20",
            TargetTemperatureCelsiusText = "80",
            TargetPowerWattsText = "100",
            TargetExperiencePriority = TargetExperiencePriority.StableFrameRate,
            TargetExperienceControlMode = TargetExperienceControlMode.RecommendOnly,
            TargetNoisePreference = NoisePreference.Quiet,
            TargetObservationWindowSecondsText = "15",
            TargetMinimumStableSamplesText = "5",
            TargetHysteresisPercentText = "7.5"
        };

        var profile = draft.ToProfile();

        Assert.True(profile.TargetExperience.Enabled);
        Assert.Equal(60, profile.TargetExperience.TargetFramesPerSecond);
        Assert.Equal(20, profile.TargetExperience.MaximumFrameTimeP95Milliseconds);
        Assert.Equal(80, profile.TargetExperience.MaximumTemperatureCelsius);
        Assert.Equal(100, profile.TargetExperience.MaximumPowerWatts);
        Assert.Equal(TargetExperiencePriority.StableFrameRate, profile.TargetExperience.Priority);
        Assert.Equal(TargetExperienceControlMode.RecommendOnly, profile.TargetExperience.ControlMode);
        Assert.Equal(NoisePreference.Quiet, profile.TargetExperience.NoisePreference);
        Assert.Equal(15, profile.TargetExperience.ObservationWindowSeconds);
        Assert.Equal(5, profile.TargetExperience.MinimumStableSamples);
        Assert.Equal(7.5, profile.TargetExperience.HysteresisPercent);
    }

    [Fact]
    public void Validate_ReturnsIssueWhenTargetExperienceValueIsInvalid()
    {
        var draft = new ProfileDraftViewModel
        {
            Name = "Invalid Target",
            ExecutableNamesText = "game.exe",
            TargetFramesPerSecondText = "0",
            TargetTemperatureCelsiusText = "not-a-number"
        };

        var issues = draft.Validate(static key => key, static (key, args) => $"{key}:{string.Join(",", args)}");

        Assert.Contains("Validation.TargetFramesPerSecondInvalid", issues);
        Assert.Contains("Validation.TargetNumericLimitInvalid", issues);
    }

    [Fact]
    public void Validate_ReturnsIssueWhenGpuClockLimitIsInvalid()
    {
        var draft = new ProfileDraftViewModel
        {
            Name = "GPU Draft",
            ExecutableNamesText = "game.exe",
            GpuMaxCoreClockMHzText = "0"
        };

        var issues = draft.Validate(static key => key, static (key, args) => $"{key}:{string.Join(",", args)}");

        Assert.Contains("Validation.GpuClockLimitInvalid", issues);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("not-a-number")]
    public void Validate_ReturnsIssueWhenProcessorMaxStateIsInvalid(string processorMaxState)
    {
        var draft = new ProfileDraftViewModel
        {
            Name = "Battery Cool Draft",
            ExecutableNamesText = "game.exe",
            ProcessorMaxStatePercentText = processorMaxState
        };

        var issues = draft.Validate(static key => key, static (key, args) => $"{key}:{string.Join(",", args)}");

        Assert.Contains("Validation.ProcessorMaxStateInvalid", issues);
    }
    [Fact]
    public void LoadAndSave_PreservesCpuSetsPreference()
    {
        var source = new PerformanceProfile
        {
            CpuSets = new CpuSetPreference
            {
                Enabled = true,
                ForegroundCpuSets = new uint[] { 0, 1, 2, 3 },
                BackgroundCpuSets = new uint[] { 4, 5 }
            }
        };
        var draft = new ProfileDraftViewModel();

        draft.LoadFromProfile(source, Array.Empty<PowerPlanOptionViewModel>());
        Assert.True(draft.CpuSetsEnabled);
        Assert.Equal("0, 1, 2, 3", draft.ForegroundCpuSetsText);
        Assert.Equal("4, 5", draft.BackgroundCpuSetsText);

        var roundTripped = draft.ToProfile();
        Assert.True(roundTripped.CpuSets.Enabled);
        Assert.Equal(source.CpuSets.ForegroundCpuSets, roundTripped.CpuSets.ForegroundCpuSets);
        Assert.Equal(source.CpuSets.BackgroundCpuSets, roundTripped.CpuSets.BackgroundCpuSets);

        draft.Reset(Array.Empty<PowerPlanOptionViewModel>());
        Assert.False(draft.CpuSetsEnabled);
        Assert.Empty(draft.ForegroundCpuSetsText);
    }

    [Fact]
    public void LoadAndSave_PreservesMemoryIoPriorityPreference()
    {
        var source = new PerformanceProfile
        {
            MemoryIoPriority = new MemoryIoPriorityPreference
            {
                Enabled = true,
                ForegroundIoPriority = IoPriorityLevel.High,
                BackgroundIoPriority = IoPriorityLevel.Low,
                ForegroundMemoryPriority = MemoryPriorityLevel.Normal,
                BackgroundMemoryPriority = MemoryPriorityLevel.VeryLow
            }
        };
        var draft = new ProfileDraftViewModel();

        draft.LoadFromProfile(source, Array.Empty<PowerPlanOptionViewModel>());
        Assert.True(draft.MemoryIoPriorityEnabled);
        Assert.Equal(IoPriorityLevel.High, draft.ForegroundIoPriority);
        Assert.Equal(IoPriorityLevel.Low, draft.BackgroundIoPriority);
        Assert.Equal(MemoryPriorityLevel.Normal, draft.ForegroundMemoryPriority);
        Assert.Equal(MemoryPriorityLevel.VeryLow, draft.BackgroundMemoryPriority);

        var roundTripped = draft.ToProfile();
        Assert.True(roundTripped.MemoryIoPriority.Enabled);
        Assert.Equal(source.MemoryIoPriority, roundTripped.MemoryIoPriority);

        draft.Reset(Array.Empty<PowerPlanOptionViewModel>());
        Assert.False(draft.MemoryIoPriorityEnabled);
    }

    [Fact]
    public void LoadAndSave_PreservesAdvancedBackgroundPolicySettings()
    {
        var source = new PerformanceProfile
        {
            BackgroundPolicies = new[]
            {
                new BackgroundProcessPolicy
                {
                    Category = "render-worker",
                    TargetPriority = PriorityLevel.Idle,
                    PreferEfficiencyMode = true,
                    TargetAffinityMask = 0x0F,
                    TargetCpuSets = new uint[] { 0, 1 },
                    TargetIoPriority = IoPriorityLevel.VeryLow,
                    TargetMemoryPriority = MemoryPriorityLevel.VeryLow,
                    TargetJobCpuRatePercent = 15.5,
                    TargetJobMemoryLimitBytes = 1024 * 1024 * 512,
                    Notes = "Restricted worker policy."
                }
            }
        };
        var draft = new ProfileDraftViewModel();

        draft.LoadFromProfile(source, Array.Empty<PowerPlanOptionViewModel>());
        var item = Assert.Single(draft.BackgroundPolicies);
        Assert.Equal("render-worker", item.Category);
        Assert.Equal(0x0F, item.TargetAffinityMask);
        Assert.Equal(new uint[] { 0, 1 }, item.TargetCpuSets);
        Assert.Equal(IoPriorityLevel.VeryLow, item.TargetIoPriority);
        Assert.Equal(MemoryPriorityLevel.VeryLow, item.TargetMemoryPriority);
        Assert.Equal(15.5, item.TargetJobCpuRatePercent);
        Assert.Equal((ulong)1024 * 1024 * 512, item.TargetJobMemoryLimitBytes);

        var roundTripped = draft.ToProfile();
        var roundTrippedPolicy = Assert.Single(roundTripped.BackgroundPolicies);
        Assert.Equal("render-worker", roundTrippedPolicy.Category);
        Assert.Equal(0x0F, roundTrippedPolicy.TargetAffinityMask);
        Assert.Equal(new uint[] { 0, 1 }, roundTrippedPolicy.TargetCpuSets);
        Assert.Equal(IoPriorityLevel.VeryLow, roundTrippedPolicy.TargetIoPriority);
        Assert.Equal(MemoryPriorityLevel.VeryLow, roundTrippedPolicy.TargetMemoryPriority);
        Assert.Equal(15.5, roundTrippedPolicy.TargetJobCpuRatePercent);
        Assert.Equal((ulong)1024 * 1024 * 512, roundTrippedPolicy.TargetJobMemoryLimitBytes);
    }
}
