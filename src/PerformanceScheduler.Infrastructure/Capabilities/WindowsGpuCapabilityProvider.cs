using System.Runtime.Versioning;
using System.Management;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Capabilities;

public sealed class WindowsGpuCapabilityProvider : IGpuCapabilityProvider
{
    public Task<GpuCapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new GpuCapabilitySnapshot
            {
                StatusKey = "Capability.GpuUnsupportedPlatform"
            });
        }

        try
        {
            var adapters = QueryAdapters();

            var adapter = adapters.FirstOrDefault();
            if (adapter is null)
            {
                return Task.FromResult(new GpuCapabilitySnapshot
                {
                    StatusKey = "Capability.GpuUnavailable"
                });
            }

            var vendor = DetectVendor(adapter.Compatibility, adapter.PnpDeviceId, adapter.Name);
            return Task.FromResult(new GpuCapabilitySnapshot
            {
                HasDetectedAdapter = true,
                AdapterName = adapter.Name ?? "Unknown",
                Vendor = vendor,
                SupportsVendorExtensions = vendor != GpuVendor.Unknown,
                SupportsClockLimit = false,
                SupportsVoltageControl = false,
                StatusKey = vendor == GpuVendor.Unknown
                    ? "Capability.GpuVendorUnknown"
                    : "Capability.GpuVendorExtensionReady"
            });
        }
        catch
        {
            return Task.FromResult(new GpuCapabilitySnapshot
            {
                StatusKey = "Capability.GpuDetectionFailed"
            });
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AdapterInfo> QueryAdapters()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController");
        using var results = searcher.Get();

        return results
            .Cast<ManagementObject>()
            .Select(result => new AdapterInfo(
                result["Name"]?.ToString(),
                result["AdapterCompatibility"]?.ToString(),
                result["PNPDeviceID"]?.ToString()))
            .Where(static adapter => !string.IsNullOrWhiteSpace(adapter.Name))
            .OrderBy(static adapter => adapter.Name?.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0)
            .ToArray();
    }

    private static GpuVendor DetectVendor(string? compatibility, string? pnpDeviceId, string? name)
    {
        var combined = string.Join(" | ", new[] { compatibility, pnpDeviceId, name }.Where(static value => !string.IsNullOrWhiteSpace(value)));

        if (combined.Contains("10DE", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Nvidia;
        }

        if (combined.Contains("1002", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("1022", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Amd;
        }

        if (combined.Contains("8086", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Intel;
        }

        return GpuVendor.Unknown;
    }

    private sealed record AdapterInfo(string? Name, string? Compatibility, string? PnpDeviceId);
}
