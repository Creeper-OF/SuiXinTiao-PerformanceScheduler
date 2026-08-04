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
