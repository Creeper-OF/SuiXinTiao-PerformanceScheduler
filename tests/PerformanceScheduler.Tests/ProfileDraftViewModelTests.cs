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
            ExecutableNamesText = "game.exe"
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
}
