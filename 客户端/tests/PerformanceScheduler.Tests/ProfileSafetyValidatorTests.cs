using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;

namespace PerformanceScheduler.Tests;

public sealed class ProfileSafetyValidatorTests
{
    [Fact]
    public void Evaluate_AllowsSafeProfile()
    {
        var result = ProfileSafetyValidator.Evaluate(CreateSafeProfile());

        Assert.True(result.CanPersist);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal(ProfileSafetyRiskLevel.Safe, result.HighestRiskLevel);
    }

    [Fact]
    public void Evaluate_BlocksProfileWithoutMatchRules()
    {
        var result = ProfileSafetyValidator.Evaluate(CreateSafeProfile() with
        {
            Match = new ProfileMatchCriteria()
        });

        var issue = Assert.Single(result.Errors);
        Assert.Equal("AtLeastOneMatchRule", issue.Code);
    }

    [Fact]
    public void Evaluate_BlocksRealTimeForegroundAndBackgroundPriorities()
    {
        var result = ProfileSafetyValidator.Evaluate(CreateSafeProfile() with
        {
            Priority = new PriorityPreference { ForegroundPriority = PriorityLevel.RealTime },
            BackgroundPolicies =
            [
                new BackgroundProcessPolicy
                {
                    Category = "default",
                    TargetPriority = PriorityLevel.RealTime
                }
            ]
        });

        Assert.False(result.CanPersist);
        Assert.Equal(2, result.Errors.Count(issue => issue.Code == "RealTimePriorityBlocked"));
        Assert.Equal(ProfileSafetyRiskLevel.Blocked, result.HighestRiskLevel);
    }

    [Fact]
    public void Evaluate_ReportsHighRiskGpuAndNonRestoringPowerWarnings()
    {
        var result = ProfileSafetyValidator.Evaluate(CreateSafeProfile() with
        {
            PowerPlan = new PowerPlanPreference
            {
                SchemeGuid = Guid.NewGuid(),
                RestoreOnExit = false
            },
            Gpu = new GpuPreference
            {
                AllowVendorApi = true,
                VoltageOffsetMv = -75
            }
        });

        Assert.True(result.CanPersist);
        Assert.Contains(result.Warnings, issue => issue.Code == "RestoreOnExitDisabled");
        Assert.Contains(result.Warnings, issue => issue.Code == "GpuVoltageRequiresApproval");
        Assert.Contains(result.Warnings, issue => issue.Code == "VendorApiRequiresApproval");
        Assert.All(result.Warnings, issue => Assert.Equal(ProfileSafetyRiskLevel.High, issue.RiskLevel));
        Assert.Equal(ProfileSafetyRiskLevel.High, result.HighestRiskLevel);
    }

    [Fact]
    public void Evaluate_WarnsWhenAutomaticTargetExperienceModeIsRequested()
    {
        var result = ProfileSafetyValidator.Evaluate(CreateSafeProfile() with
        {
            TargetExperience = new TargetExperiencePreference
            {
                Enabled = true,
                TargetFramesPerSecond = 60,
                ControlMode = TargetExperienceControlMode.Automatic
            }
        });

        Assert.Contains(result.Warnings, issue => issue.Code == "TargetAutomaticModeRequiresVerifiedProvider");
        Assert.Equal(ProfileSafetyRiskLevel.High, result.HighestRiskLevel);
    }

    [Fact]
    public void Evaluate_WarnsWhenProfileMatchesProtectedWindowsProcess()
    {
        var result = ProfileSafetyValidator.Evaluate(CreateSafeProfile() with
        {
            Match = new ProfileMatchCriteria
            {
                ExecutableNames = new[] { "lsass.exe" }
            }
        });

        var issue = Assert.Single(result.Warnings);
        Assert.Equal("ProtectedProcessMatchRequiresReview", issue.Code);
        Assert.Equal(ProfileSafetyRiskLevel.High, issue.RiskLevel);
        Assert.Equal(ProfileSafetyRiskLevel.High, result.HighestRiskLevel);
    }

    [Fact]
    public void PrepareUntrustedImport_DisablesEvenOtherwiseSafeProfile()
    {
        var validation = ProfileSafetyValidator.EvaluateUntrustedImport(CreateSafeProfile());
        var prepared = ProfileSafetyValidator.PrepareUntrustedImport(CreateSafeProfile());

        Assert.True(validation.CanPersist);
        Assert.Contains(validation.Warnings, issue => issue.Code == "UntrustedImportSource");
        Assert.Equal(ProfileSafetyRiskLevel.High, validation.HighestRiskLevel);
        Assert.False(prepared.IsEnabled);
    }

    [Fact]
    public void EvaluateLibrary_BlocksDuplicateIdsAndMultipleEnabledGlobalDefaults()
    {
        var first = CreateSafeProfile() with
        {
            Id = "duplicate",
            Name = "Global One",
            IsGlobalDefault = true
        };
        var second = CreateSafeProfile() with
        {
            Id = "duplicate",
            Name = "Global Two",
            IsGlobalDefault = true
        };

        var result = ProfileSafetyValidator.EvaluateLibrary([first, second]);

        Assert.Contains(result.Errors, issue => issue.Code == "DuplicateProfileId");
        Assert.Contains(result.Errors, issue => issue.Code == "MultipleEnabledGlobalDefaults");
    }

    [Fact]
    public void EvaluateLibrary_AllowsSimultaneousAcAndBatteryGlobalDefaults()
    {
        var acDefault = CreateSafeProfile() with
        {
            Id = "ac-default",
            Name = "Global AC",
            IsGlobalDefault = true,
            PowerSourceMode = PowerSourceMode.Ac
        };
        var batteryDefault = CreateSafeProfile() with
        {
            Id = "dc-default",
            Name = "Global DC",
            IsGlobalDefault = true,
            PowerSourceMode = PowerSourceMode.Battery
        };

        var result = ProfileSafetyValidator.EvaluateLibrary([acDefault, batteryDefault]);

        Assert.DoesNotContain(result.Errors, issue => issue.Code == "MultipleEnabledGlobalDefaults");
    }

    private static PerformanceProfile CreateSafeProfile() => new()
    {
        Id = "safe-profile",
        Name = "Safe Profile",
        Match = new ProfileMatchCriteria
        {
            ExecutableNames = new[] { "game.exe" },
            ProcessNames = new[] { "game" }
        }
    };
}
