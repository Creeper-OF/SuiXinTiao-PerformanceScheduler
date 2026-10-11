using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Hardware.Gpu;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.AmdAdlx;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;
using Xunit;

namespace PerformanceScheduler.Tests;

public sealed class AmdGpuTuningProviderTests
{
    [Fact]
    public async Task GetSupportAsync_WhenClockLockingSupported_ReturnsReversibleLimitReady()
    {
        var fakeSource = new FakeAmdSource
        {
            Availability = new AmdGpuAvailability
            {
                IsAvailable = true,
                DeviceCount = 1,
                SupportsClockLocking = true
            }
        };

        var provider = new AmdGpuTuningProvider(fakeSource);
        var support = await provider.GetSupportAsync(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            Vendor = GpuVendor.Amd
        });

        Assert.Equal("AMD ADLX", support.ProviderName);
        Assert.True(support.SupportsApplyPipeline);
        Assert.True(support.SupportsVendorExtensions);
        Assert.True(support.SupportsClockLimit);
        Assert.False(support.SupportsVoltageControl);
        Assert.Equal("Capability.GpuReversibleLimitReady", support.StatusKey);
        Assert.Contains(GpuTuningFeature.Telemetry, support.SupportedFeatures);
        Assert.Contains(GpuTuningFeature.ResetToDefault, support.SupportedFeatures);
        Assert.Contains(GpuTuningFeature.CoreClockLimit, support.SupportedFeatures);
    }

    [Fact]
    public async Task GetSupportAsync_WhenClockLockingNotSupported_ReturnsReadOnlyTelemetry()
    {
        var fakeSource = new FakeAmdSource
        {
            Availability = new AmdGpuAvailability
            {
                IsAvailable = true,
                DeviceCount = 1,
                SupportsClockLocking = false
            }
        };

        var provider = new AmdGpuTuningProvider(fakeSource);
        var support = await provider.GetSupportAsync(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            Vendor = GpuVendor.Amd
        });

        Assert.Equal("AMD ADLX", support.ProviderName);
        Assert.False(support.SupportsApplyPipeline);
        Assert.True(support.SupportsVendorExtensions);
        Assert.False(support.SupportsClockLimit);
        Assert.Equal("Capability.GpuReadOnlyTelemetry", support.StatusKey);
        Assert.Single(support.SupportedFeatures, GpuTuningFeature.Telemetry);
    }

    [Fact]
    public async Task GetSupportAsync_WhenAmdUnavailable_ReturnsGpuUnavailable()
    {
        var fakeSource = new FakeAmdSource
        {
            Availability = new AmdGpuAvailability
            {
                IsAvailable = false,
                FailureReason = "adlx64.dll not found."
            }
        };

        var provider = new AmdGpuTuningProvider(fakeSource);
        var support = await provider.GetSupportAsync(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            Vendor = GpuVendor.Amd
        });

        Assert.Equal("AMD ADLX", support.ProviderName);
        Assert.False(support.SupportsApplyPipeline);
        Assert.True(support.SupportsVendorExtensions);
        Assert.Empty(support.SupportedFeatures);
        Assert.Equal("Capability.GpuUnavailable", support.StatusKey);
    }

    [Fact]
    public async Task TrySetLockedClocksAsync_DelegatesToSource()
    {
        var fakeSource = new FakeAmdSource();
        var provider = new AmdGpuTuningProvider(fakeSource);

        var result = await provider.TrySetLockedClocksAsync(500, 1500);

        Assert.True(result);
        Assert.Equal(500u, fakeSource.LastSetMinClock);
        Assert.Equal(1500u, fakeSource.LastSetMaxClock);
    }

    [Fact]
    public async Task TryResetLockedClocksAsync_DelegatesToSource()
    {
        var fakeSource = new FakeAmdSource();
        var provider = new AmdGpuTuningProvider(fakeSource);
        await provider.TrySetLockedClocksAsync(500, 1500);

        var result = await provider.TryResetLockedClocksAsync();

        Assert.True(result);
        Assert.True(fakeSource.ResetCalled);
    }

    [Fact]
    public async Task RecoveryStore_MarkPendingAndClear_WorkCorrectly()
    {
        var fakeSource = new FakeAmdSource();
        var fakeStore = new FakeRecoveryStore();
        var provider = new AmdGpuTuningProvider(fakeSource, fakeStore);

        Assert.False(await fakeStore.IsPendingAsync());

        var setResult = await provider.TrySetLockedClocksAsync(600, 1600);
        Assert.True(setResult);
        Assert.True(await fakeStore.IsPendingAsync());

        var resetResult = await provider.TryResetLockedClocksAsync();
        Assert.True(resetResult);
        Assert.False(await fakeStore.IsPendingAsync());
    }

    private sealed class FakeAmdSource : IAmdGpuSource
    {
        public AmdGpuAvailability Availability { get; init; } = new() { IsAvailable = true, SupportsClockLocking = true };
        public uint? LastSetMinClock { get; private set; }
        public uint? LastSetMaxClock { get; private set; }
        public bool ResetCalled { get; private set; }

        public Task<AmdGpuAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Availability);

        public Task<AmdGpuSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AmdGpuSnapshot());

        public Task<bool> TrySetLockedClocksAsync(
            uint minGpuClockMHz,
            uint maxGpuClockMHz,
            CancellationToken cancellationToken = default)
        {
            LastSetMinClock = minGpuClockMHz;
            LastSetMaxClock = maxGpuClockMHz;
            return Task.FromResult(true);
        }

        public Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default)
        {
            ResetCalled = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRecoveryStore : IGpuClockRecoveryStore
    {
        private bool _isPending;

        public Task<bool> IsPendingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_isPending);

        public Task MarkPendingAsync(CancellationToken cancellationToken = default)
        {
            _isPending = true;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _isPending = false;
            return Task.CompletedTask;
        }
    }
}
