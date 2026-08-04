using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.Composition;

namespace PerformanceScheduler.Tests;

public sealed class GpuTuningProviderRegistryTests
{
    [Theory]
    [InlineData(GpuVendor.Amd, "AMD ADLX")]
    [InlineData(GpuVendor.Nvidia, "NVIDIA NVML")]
    [InlineData(GpuVendor.Intel, "Intel Level Zero Sysman")]
    public async Task Resolve_KnownVendor_ReturnsPlannedProvider(
        GpuVendor vendor,
        string expectedProviderName)
    {
        var provider = new GpuTuningProviderRegistry().Resolve(vendor);

        var support = await provider.GetSupportAsync(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            Vendor = vendor
        });

        Assert.Equal(expectedProviderName, provider.ProviderName);
        Assert.Equal(expectedProviderName, support.ProviderName);
        Assert.True(support.SupportsVendorExtensions);
        Assert.False(support.SupportsApplyPipeline);
        Assert.Empty(support.SupportedFeatures);
    }

    [Fact]
    public async Task Resolve_UnknownVendor_ReturnsUnsupportedProvider()
    {
        var provider = new GpuTuningProviderRegistry().Resolve(GpuVendor.Unknown);

        var support = await provider.GetSupportAsync(new GpuCapabilitySnapshot
        {
            HasDetectedAdapter = true,
            Vendor = GpuVendor.Unknown
        });

        Assert.Equal("Unavailable", provider.ProviderName);
        Assert.False(support.SupportsVendorExtensions);
        Assert.False(support.SupportsApplyPipeline);
        Assert.Equal("Capability.GpuVendorUnknown", support.StatusKey);
    }

    [Fact]
    public void SupportedFeatures_DeriveExistingUiCapabilityFlags()
    {
        var support = new GpuTuningProviderSupport
        {
            SupportedFeatures =
            [
                GpuTuningFeature.CoreClockLimit,
                GpuTuningFeature.VoltageOffset
            ]
        };

        Assert.True(support.SupportsClockLimit);
        Assert.True(support.SupportsVoltageControl);
    }
}
