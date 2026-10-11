using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class BackgroundPolicySelectorTests
{
    [Fact]
    public void Select_ReturnsExplicitCategoryMatchBeforeDefault()
    {
        var selector = new BackgroundPolicySelector();
        var policies = new[]
        {
            new BackgroundProcessPolicy { Category = "default", TargetPriority = PriorityLevel.BelowNormal },
            new BackgroundProcessPolicy { Category = "browser", TargetPriority = PriorityLevel.Idle }
        };

        var result = selector.Select(ProcessClassification.Browser, policies);

        Assert.NotNull(result);
        Assert.Equal(PriorityLevel.Idle, result!.TargetPriority);
    }

    [Fact]
    public void Select_ReturnsDefaultWhenNoExplicitCategoryExists()
    {
        var selector = new BackgroundPolicySelector();
        var policies = new[]
        {
            new BackgroundProcessPolicy { Category = "default", TargetPriority = PriorityLevel.BelowNormal }
        };

        var result = selector.Select(ProcessClassification.Communication, policies);

        Assert.NotNull(result);
        Assert.Equal(PriorityLevel.BelowNormal, result!.TargetPriority);
    }
}
