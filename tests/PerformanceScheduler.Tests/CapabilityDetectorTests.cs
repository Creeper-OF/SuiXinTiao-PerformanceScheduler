using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Capabilities;

namespace PerformanceScheduler.Tests;

public sealed class CapabilityDetectorTests
{
    [Fact]
    public async Task DetectAsync_UsesRegisteredGpuProviderSupport()
    {
        var detector = new CapabilityDetector(
            new StubPowerPlanManager(),
            new StubGpuCapabilityProvider(new GpuCapabilitySnapshot
            {
                HasDetectedAdapter = true,
                AdapterName = "GeForce Test Adapter",
                Vendor = GpuVendor.Nvidia
            }),
            new StubGpuTuningProviderRegistry(new StubGpuTuningProvider()));

        var result = await detector.DetectAsync();

        Assert.Equal("NVIDIA Test Provider", result.Gpu.ProviderName);
        Assert.True(result.Gpu.SupportsVendorExtensions);
        Assert.False(result.Gpu.SupportsApplyPipeline);
        Assert.False(result.Gpu.SupportsClockLimit);
        Assert.False(result.Gpu.SupportsVoltageControl);
        Assert.Equal("Capability.GpuProviderPlaceholder", result.Gpu.StatusKey);
    }

    [Fact]
    public async Task DetectAsync_UsesCachedSnapshotWithinCacheDuration()
    {
        var powerPlanManager = new StubPowerPlanManager();
        var gpuCapabilityProvider = new StubGpuCapabilityProvider(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            AdapterName = "GeForce Test Adapter",
            Vendor = GpuVendor.Nvidia
        });
        var gpuTuningProvider = new StubGpuTuningProvider();
        var detector = new CapabilityDetector(
            powerPlanManager,
            gpuCapabilityProvider,
            new StubGpuTuningProviderRegistry(gpuTuningProvider),
            TimeSpan.FromMinutes(1));

        var first = await detector.DetectAsync();
        var second = await detector.DetectAsync();

        Assert.Same(first, second);
        Assert.Equal(1, powerPlanManager.GetAvailablePlansCallCount);
        Assert.Equal(1, gpuCapabilityProvider.DetectCallCount);
        Assert.Equal(1, gpuTuningProvider.GetSupportCallCount);
    }

    private sealed class StubPowerPlanManager : IPowerPlanManager
    {
        public int GetAvailablePlansCallCount { get; private set; }

        public Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default)
        {
            GetAvailablePlansCallCount++;
            return Task.FromResult<IReadOnlyList<PowerPlanInfo>>(Array.Empty<PowerPlanInfo>());
        }

        public Task<PowerPlanInfo?> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PowerPlanInfo?>(null);

        public Task<bool> SetActivePlanAsync(Guid schemeGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<PowerPlanAdvancedState?> GetAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PowerPlanAdvancedState?>(null);

        public Task<bool> ApplyAdvancedSettingsAsync(
            Guid schemeGuid,
            PowerSourceMode powerSourceMode,
            PowerPlanAdvancedPreference preference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> RestoreAdvancedSettingsAsync(
            PowerPlanAdvancedState state,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class StubGpuCapabilityProvider : IGpuCapabilityProvider
    {
        private readonly GpuCapabilitySnapshot _snapshot;

        public StubGpuCapabilityProvider(GpuCapabilitySnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public int DetectCallCount { get; private set; }

        public Task<GpuCapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default)
        {
            DetectCallCount++;
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class StubGpuTuningProviderRegistry : IGpuTuningProviderRegistry
    {
        private readonly IGpuTuningProvider _provider;

        public StubGpuTuningProviderRegistry(IGpuTuningProvider provider)
        {
            _provider = provider;
        }

        public IGpuTuningProvider Resolve(GpuVendor vendor) => _provider;
    }

    private sealed class StubGpuTuningProvider : IGpuTuningProvider
    {
        public GpuVendor Vendor => GpuVendor.Nvidia;

        public string ProviderName => "NVIDIA Test Provider";

        public int GetSupportCallCount { get; private set; }

        public Task<GpuTuningProviderSupport> GetSupportAsync(
            GpuCapabilitySnapshot detectedGpu,
            CancellationToken cancellationToken = default)
        {
            GetSupportCallCount++;
            return Task.FromResult(new GpuTuningProviderSupport
            {
                ProviderName = ProviderName,
                SupportsVendorExtensions = true,
                SupportsApplyPipeline = false,
                SupportsClockLimit = false,
                SupportsVoltageControl = false,
                StatusKey = "Capability.GpuProviderPlaceholder"
            });
        }
    }
}
