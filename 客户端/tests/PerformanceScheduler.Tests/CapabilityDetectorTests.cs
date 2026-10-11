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

    [Fact]
    public async Task InvalidateCache_ForcesNextDetection()
    {
        var powerPlanManager = new StubPowerPlanManager();
        var gpuCapabilityProvider = new StubGpuCapabilityProvider(new GpuCapabilitySnapshot());
        var detector = new CapabilityDetector(
            powerPlanManager,
            gpuCapabilityProvider,
            new StubGpuTuningProviderRegistry(new StubGpuTuningProvider()),
            TimeSpan.FromMinutes(1));

        await detector.DetectAsync();
        detector.InvalidateCache();
        await detector.DetectAsync();

        Assert.Equal(2, powerPlanManager.GetAvailablePlansCallCount);
        Assert.Equal(2, gpuCapabilityProvider.DetectCallCount);
    }

    [Fact]
    public async Task InvalidateCache_DoesNotAllowInFlightDetectionToRepopulateCache()
    {
        var detectionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detectionCompletion = new TaskCompletionSource<GpuCapabilitySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gpuCapabilityProvider = new StubGpuCapabilityProvider(
            new GpuCapabilitySnapshot(),
            detectCompletion: detectionCompletion,
            detectStarted: detectionStarted);
        var detector = new CapabilityDetector(
            new StubPowerPlanManager(),
            gpuCapabilityProvider,
            new StubGpuTuningProviderRegistry(new StubGpuTuningProvider()),
            TimeSpan.FromMinutes(1));

        var inFlightDetection = detector.DetectAsync();
        await detectionStarted.Task;
        detector.InvalidateCache();
        detectionCompletion.SetResult(new GpuCapabilitySnapshot());
        await inFlightDetection;

        await detector.DetectAsync();

        Assert.Equal(2, gpuCapabilityProvider.DetectCallCount);
    }

    [Fact]
    public async Task DetectAsync_ReturnsStableReasonCodesForUnsupportedCapabilities()
    {
        var detector = new CapabilityDetector(
            new StubPowerPlanManager(),
            new StubGpuCapabilityProvider(new GpuCapabilitySnapshot()),
            new StubGpuTuningProviderRegistry(new StubGpuTuningProvider()));

        var result = await detector.DetectAsync();

        Assert.Contains(CapabilityReasonCodes.NoPowerPlans, result.UnsupportedReasons);
        Assert.Contains(CapabilityReasonCodes.GpuAdapterUnavailable, result.UnsupportedReasons);
    }

    [Fact]
    public async Task DetectAsync_DegradesWhenPowerProviderIsUnavailable()
    {
        var detector = new CapabilityDetector(
            new StubPowerPlanManager(throwOnGetAvailablePlans: true),
            new StubGpuCapabilityProvider(new GpuCapabilitySnapshot()),
            new StubGpuTuningProviderRegistry(new StubGpuTuningProvider()));

        var result = await detector.DetectAsync();

        Assert.False(result.SupportsPowerPlanSwitching);
        Assert.Contains(CapabilityReasonCodes.NoPowerPlans, result.UnsupportedReasons);
    }

    [Fact]
    public async Task DetectAsync_DegradesWhenGpuDetectionFails()
    {
        var detector = new CapabilityDetector(
            new StubPowerPlanManager(),
            new StubGpuCapabilityProvider(new GpuCapabilitySnapshot(), throwOnDetect: true),
            new StubGpuTuningProviderRegistry(new StubGpuTuningProvider()));

        var result = await detector.DetectAsync();

        Assert.False(result.Gpu.HasDetectedAdapter);
        Assert.Equal(CapabilityReasonCodes.GpuDetectionUnavailable, result.Gpu.StatusKey);
        Assert.Contains(CapabilityReasonCodes.GpuDetectionUnavailable, result.UnsupportedReasons);
    }

    [Fact]
    public async Task DetectAsync_DegradesWhenGpuProviderSupportFails()
    {
        var detector = new CapabilityDetector(
            new StubPowerPlanManager(),
            new StubGpuCapabilityProvider(new GpuCapabilitySnapshot
            {
                HasDetectedAdapter = true,
                AdapterName = "GeForce Test Adapter",
                Vendor = GpuVendor.Nvidia
            }),
            new StubGpuTuningProviderRegistry(new StubGpuTuningProvider(throwOnGetSupport: true)));

        var result = await detector.DetectAsync();

        Assert.True(result.Gpu.HasDetectedAdapter);
        Assert.False(result.Gpu.SupportsVendorExtensions);
        Assert.Equal(CapabilityReasonCodes.GpuProviderUnavailable, result.Gpu.StatusKey);
        Assert.Contains(CapabilityReasonCodes.GpuProviderUnavailable, result.UnsupportedReasons);
    }

    [Fact]
    public async Task DetectAsync_ObservesCancellationWhenCachedSnapshotAlreadyExists()
    {
        var detector = new CapabilityDetector(
            new StubPowerPlanManager(),
            new StubGpuCapabilityProvider(new GpuCapabilitySnapshot()),
            new StubGpuTuningProviderRegistry(new StubGpuTuningProvider()),
            TimeSpan.FromMinutes(1));

        await detector.DetectAsync();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detector.DetectAsync(cancellationSource.Token));
    }

    [Fact]
    public async Task DetectAsync_ObservesCancellationAfterGpuDetectionReturns()
    {
        var detectionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detectionCompletion = new TaskCompletionSource<GpuCapabilitySnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new CapabilityDetector(
            new StubPowerPlanManager(),
            new StubGpuCapabilityProvider(
                new GpuCapabilitySnapshot(),
                detectCompletion: detectionCompletion,
                detectStarted: detectionStarted),
            new StubGpuTuningProviderRegistry(new StubGpuTuningProvider()));
        using var cancellationSource = new CancellationTokenSource();

        var detectionTask = detector.DetectAsync(cancellationSource.Token);
        await detectionStarted.Task;
        cancellationSource.Cancel();
        detectionCompletion.SetResult(new GpuCapabilitySnapshot());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detectionTask);
    }

    [Fact]
    public async Task DetectAsync_ObservesCancellationAfterGpuProviderReturns()
    {
        var supportStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var supportCompletion = new TaskCompletionSource<GpuTuningProviderSupport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new CapabilityDetector(
            new StubPowerPlanManager(),
            new StubGpuCapabilityProvider(new GpuCapabilitySnapshot
            {
                HasDetectedAdapter = true,
                AdapterName = "GeForce Test Adapter",
                Vendor = GpuVendor.Nvidia
            }),
            new StubGpuTuningProviderRegistry(
                new StubGpuTuningProvider(
                    supportCompletion: supportCompletion,
                    supportStarted: supportStarted)));
        using var cancellationSource = new CancellationTokenSource();

        var detectionTask = detector.DetectAsync(cancellationSource.Token);
        await supportStarted.Task;
        cancellationSource.Cancel();
        supportCompletion.SetResult(new GpuTuningProviderSupport
        {
            ProviderName = "NVIDIA Test Provider",
            SupportsVendorExtensions = true,
            SupportsApplyPipeline = false,
            StatusKey = "Capability.GpuProviderPlaceholder"
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detectionTask);
    }

    private sealed class StubPowerPlanManager : IPowerPlanManager
    {
        private readonly bool _throwOnGetAvailablePlans;

        public StubPowerPlanManager(bool throwOnGetAvailablePlans = false)
        {
            _throwOnGetAvailablePlans = throwOnGetAvailablePlans;
        }

        public int GetAvailablePlansCallCount { get; private set; }

        public Task<IReadOnlyList<PowerPlanInfo>> GetAvailablePlansAsync(CancellationToken cancellationToken = default)
        {
            GetAvailablePlansCallCount++;
            if (_throwOnGetAvailablePlans)
            {
                throw new InvalidOperationException("Test power provider failure.");
            }

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
        private readonly bool _throwOnDetect;
        private readonly Task<GpuCapabilitySnapshot>? _detectCompletion;
        private readonly TaskCompletionSource<bool>? _detectStarted;

        public StubGpuCapabilityProvider(
            GpuCapabilitySnapshot snapshot,
            bool throwOnDetect = false,
            TaskCompletionSource<GpuCapabilitySnapshot>? detectCompletion = null,
            TaskCompletionSource<bool>? detectStarted = null)
        {
            _snapshot = snapshot;
            _throwOnDetect = throwOnDetect;
            _detectCompletion = detectCompletion?.Task;
            _detectStarted = detectStarted;
        }

        public int DetectCallCount { get; private set; }

        public Task<GpuCapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default)
        {
            DetectCallCount++;
            if (_throwOnDetect)
            {
                throw new InvalidOperationException("Test GPU detection failure.");
            }

            if (_detectCompletion is not null)
            {
                _detectStarted?.TrySetResult(true);
                return _detectCompletion;
            }

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
        private readonly bool _throwOnGetSupport;
        private readonly Task<GpuTuningProviderSupport>? _supportCompletion;
        private readonly TaskCompletionSource<bool>? _supportStarted;

        public StubGpuTuningProvider(
            bool throwOnGetSupport = false,
            TaskCompletionSource<GpuTuningProviderSupport>? supportCompletion = null,
            TaskCompletionSource<bool>? supportStarted = null)
        {
            _throwOnGetSupport = throwOnGetSupport;
            _supportCompletion = supportCompletion?.Task;
            _supportStarted = supportStarted;
        }

        public GpuVendor Vendor => GpuVendor.Nvidia;

        public string ProviderName => "NVIDIA Test Provider";

        public int GetSupportCallCount { get; private set; }

        public Task<GpuTuningProviderSupport> GetSupportAsync(
            GpuCapabilitySnapshot detectedGpu,
            CancellationToken cancellationToken = default)
        {
            GetSupportCallCount++;
            if (_throwOnGetSupport)
            {
                throw new InvalidOperationException("Test GPU provider failure.");
            }

            if (_supportCompletion is not null)
            {
                _supportStarted?.TrySetResult(true);
                return _supportCompletion;
            }

            return Task.FromResult(new GpuTuningProviderSupport
            {
                ProviderName = ProviderName,
                SupportsVendorExtensions = true,
                SupportsApplyPipeline = false,
                StatusKey = "Capability.GpuProviderPlaceholder"
            });
        }
    }
}
