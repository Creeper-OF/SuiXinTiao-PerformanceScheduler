using System.Management;
using System.Runtime.Versioning;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Hardware;

public sealed class WindowsDeviceFingerprintProvider : IDeviceFingerprintProvider
{
    public Task<DeviceFingerprint> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new DeviceFingerprint());
        }

        try
        {
            var computerSystem = QueryFirst("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
            var processor = QueryFirst("SELECT Name FROM Win32_Processor");
            var gpuNames = QueryGpuNames();

            return Task.FromResult(new DeviceFingerprint
            {
                Manufacturer = GetValue(computerSystem, "Manufacturer"),
                Model = GetValue(computerSystem, "Model"),
                CpuName = GetValue(processor, "Name"),
                GpuNames = gpuNames,
                TotalMemoryBytes = TryParseUlong(GetValue(computerSystem, "TotalPhysicalMemory"))
            });
        }
        catch
        {
            return Task.FromResult(new DeviceFingerprint());
        }
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string> QueryFirst(string query)
    {
        using var searcher = new ManagementObjectSearcher(query);
        using var results = searcher.Get();
        var result = results.Cast<ManagementObject>().FirstOrDefault();
        if (result is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return result.Properties
            .Cast<PropertyData>()
            .Where(static property => property.Value is not null)
            .ToDictionary(
                static property => property.Name,
                static property => property.Value?.ToString() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> QueryGpuNames()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
        using var results = searcher.Get();

        return results
            .Cast<ManagementObject>()
            .Select(static result => result["Name"]?.ToString())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ulong? TryParseUlong(string rawValue) =>
        ulong.TryParse(rawValue, out var value) ? value : null;

    private static string GetValue(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;
}
