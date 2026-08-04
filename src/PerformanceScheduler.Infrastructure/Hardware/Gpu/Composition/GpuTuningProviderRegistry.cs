using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.Composition;

public sealed class GpuTuningProviderRegistry : IGpuTuningProviderRegistry
{
    private readonly IReadOnlyDictionary<GpuVendor, IGpuTuningProvider> _providers;
    private readonly IGpuTuningProvider _fallbackProvider = new UnsupportedGpuTuningProvider();

    public GpuTuningProviderRegistry(IEnumerable<IGpuTuningProvider>? providers = null)
    {
        _providers = (providers ?? CreateDefaults())
            .GroupBy(provider => provider.Vendor)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public IGpuTuningProvider Resolve(GpuVendor vendor) =>
        _providers.TryGetValue(vendor, out var provider)
            ? provider
            : _fallbackProvider;

    private static IEnumerable<IGpuTuningProvider> CreateDefaults()
    {
        yield return new NvidiaGpuTuningProvider();
        yield return new AmdGpuTuningProvider();
        yield return new IntelGpuTuningProvider();
    }
}
