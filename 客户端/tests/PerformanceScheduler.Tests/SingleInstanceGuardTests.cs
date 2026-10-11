using PerformanceScheduler.App.Services;

namespace PerformanceScheduler.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_AllowsOnlyOneOwnerAndReleasesForTheNextOwner()
    {
        var mutexName = $"Local\\PerformanceScheduler.Tests.{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(mutexName, out var first));
        using (first)
        {
            Assert.NotNull(first);
            Assert.False(SingleInstanceGuard.TryAcquire(mutexName, out var second));
            Assert.Null(second);
        }

        Assert.True(SingleInstanceGuard.TryAcquire(mutexName, out var third));
        third?.Dispose();
    }
}
