using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;

namespace PerformanceScheduler.Tests;

public sealed class NvidiaGpuTuningProviderTests
{
    [Fact]
    public async Task GetSupportAsync_WhenClockLockingSupported_ReturnsReversibleLimitReady()
    {
        var fakeSource = new FakeNvmlSource
        {
            Availability = new NvidiaNvmlAvailability
            {
                IsAvailable = true,
                DeviceCount = 1,
                SupportsClockLocking = true
            }
        };

        var provider = new NvidiaGpuTuningProvider(fakeSource);
        var support = await provider.GetSupportAsync(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            Vendor = GpuVendor.Nvidia
        });

        Assert.Equal("NVIDIA NVML", support.ProviderName);
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
        var fakeSource = new FakeNvmlSource
        {
            Availability = new NvidiaNvmlAvailability
            {
                IsAvailable = true,
                DeviceCount = 1,
                SupportsClockLocking = false
            }
        };

        var provider = new NvidiaGpuTuningProvider(fakeSource);
        var support = await provider.GetSupportAsync(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            Vendor = GpuVendor.Nvidia
        });

        Assert.Equal("NVIDIA NVML", support.ProviderName);
        Assert.False(support.SupportsApplyPipeline);
        Assert.True(support.SupportsVendorExtensions);
        Assert.False(support.SupportsClockLimit);
        Assert.Equal("Capability.GpuReadOnlyTelemetry", support.StatusKey);
        Assert.Single(support.SupportedFeatures, GpuTuningFeature.Telemetry);
    }

    [Fact]
    public async Task GetSupportAsync_WhenNvmlUnavailable_ReturnsGpuUnavailable()
    {
        var fakeSource = new FakeNvmlSource
        {
            Availability = new NvidiaNvmlAvailability
            {
                IsAvailable = false,
                FailureReason = "nvml.dll not found."
            }
        };

        var provider = new NvidiaGpuTuningProvider(fakeSource);
        var support = await provider.GetSupportAsync(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            Vendor = GpuVendor.Nvidia
        });

        Assert.Equal("NVIDIA NVML", support.ProviderName);
        Assert.False(support.SupportsApplyPipeline);
        Assert.False(support.SupportsVendorExtensions);
        Assert.Empty(support.SupportedFeatures);
        Assert.Equal("Capability.GpuUnavailable", support.StatusKey);
    }

    [Fact]
    public async Task TrySetLockedClocksAsync_DelegatesToSource()
    {
        var fakeSource = new FakeNvmlSource();
        var provider = new NvidiaGpuTuningProvider(fakeSource);

        var result = await provider.TrySetLockedClocksAsync(210, 800);

        Assert.True(result);
        Assert.Equal(210u, fakeSource.LastSetMinClock);
        Assert.Equal(800u, fakeSource.LastSetMaxClock);
    }

    [Fact]
    public async Task TryResetLockedClocksAsync_DelegatesToSource()
    {
        var fakeSource = new FakeNvmlSource();
        var provider = new NvidiaGpuTuningProvider(fakeSource);
        await provider.TrySetLockedClocksAsync(210, 800);

        var result = await provider.TryResetLockedClocksAsync();

        Assert.True(result);
        Assert.True(fakeSource.ResetCalled);
    }

    private sealed class FakeNvmlSource : INvidiaNvmlSource
    {
        public NvidiaNvmlAvailability Availability { get; init; } = new() { IsAvailable = true, SupportsClockLocking = true };
        public uint? LastSetMinClock { get; private set; }
        public uint? LastSetMaxClock { get; private set; }
        public bool ResetCalled { get; private set; }

        public Task<NvidiaNvmlAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Availability);

        public Task<NvidiaNvmlSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NvidiaNvmlSnapshot());

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
}
