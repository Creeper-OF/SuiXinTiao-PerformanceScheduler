using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Infrastructure.Processes;
using PerformanceScheduler.Infrastructure.Telemetry.WindowsGpu;

namespace PerformanceScheduler.Tests;

public sealed class WindowsResourceArbitrationCandidateProviderTests
{
    [Fact]
    public async Task CollectAsync_ThrowsOnCancellation()
    {
        var provider = new WindowsResourceArbitrationCandidateProvider();
        var foreground = new FocusedAppContext
        {
            ProcessId = 999999,
            ProcessName = "test-fg",
            WindowTitle = "Test",
            WindowHandle = (nint)1
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.CollectAsync(foreground, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CollectAsync_WithoutGpuSource_ReturnsCandidatesWithNullGpuUsage()
    {
        var provider = new WindowsResourceArbitrationCandidateProvider();
        var foreground = new FocusedAppContext
        {
            ProcessId = Environment.ProcessId,
            ProcessName = "self",
            WindowTitle = "Self",
            WindowHandle = (nint)1
        };

        var candidates = await provider.CollectAsync(foreground);

        Assert.NotNull(candidates);
        // Excludes self and foreground
        Assert.DoesNotContain(candidates, c => c.ProcessId == Environment.ProcessId);
        // All candidates should have null GpuUsagePercent when no gpuSource is configured
        Assert.All(candidates, c => Assert.Null(c.GpuUsagePercent));
    }

    [Fact]
    public async Task CollectAsync_WithGpuSource_PopulatesGpuUsageForEligibleCandidates()
    {
        var fakeGpuSource = new FakeGpuCounterSource(usagePercent: 35.0);
        var provider = new WindowsResourceArbitrationCandidateProvider(fakeGpuSource);

        var foreground = new FocusedAppContext
        {
            ProcessId = Environment.ProcessId,
            ProcessName = "self",
            WindowTitle = "Self",
            WindowHandle = (nint)1
        };

        var profile = new PerformanceProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            Priority = new PriorityPreference
            {
                LowerBackgroundProcesses = true
            }
        };

        var candidates = await provider.CollectAsync(foreground, profile);

        Assert.NotNull(candidates);
        var eligibleCandidates = candidates.Where(c => !c.IsProtected && c.HasApplicablePolicy).ToList();
        if (eligibleCandidates.Count > 0)
        {
            Assert.Contains(eligibleCandidates, c => c.GpuUsagePercent == 35.0);
        }
    }

    [Fact]
    public async Task CollectAsync_WithGpuSource_WhenGpuSourceThrows_GracefullyHandlesFailure()
    {
        var throwingGpuSource = new FakeGpuCounterSource(shouldThrow: true);
        var provider = new WindowsResourceArbitrationCandidateProvider(throwingGpuSource);

        var foreground = new FocusedAppContext
        {
            ProcessId = Environment.ProcessId,
            ProcessName = "self",
            WindowTitle = "Self",
            WindowHandle = (nint)1
        };

        var profile = new PerformanceProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            Priority = new PriorityPreference
            {
                LowerBackgroundProcesses = true
            }
        };

        var candidates = await provider.CollectAsync(foreground, profile);

        Assert.NotNull(candidates);
        // None should fail the entire collection; GPU usage should simply be null
        Assert.All(candidates, c => Assert.Null(c.GpuUsagePercent));
    }

    private sealed class FakeGpuCounterSource : IWindowsGpuPerformanceCounterSource
    {
        private readonly double? _usagePercent;
        private readonly bool _shouldThrow;

        public FakeGpuCounterSource(double? usagePercent = null, bool shouldThrow = false)
        {
            _usagePercent = usagePercent;
            _shouldThrow = shouldThrow;
        }

        public Task<WindowsGpuPerformanceCounterAvailability> GetAvailabilityAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WindowsGpuPerformanceCounterAvailability
            {
                SupportsEngineUtilization = true,
                SupportsProcessMemory = true
            });

        public Task<GpuProcessPerformanceSnapshot> CollectProcessAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            if (_shouldThrow)
            {
                throw new InvalidOperationException("Simulated counter query failure");
            }

            return Task.FromResult(new GpuProcessPerformanceSnapshot
            {
                UsagePercent = _usagePercent
            });
        }
    }
}
