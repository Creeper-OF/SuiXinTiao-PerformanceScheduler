using System.Globalization;
using System.Management;
using System.Runtime.Versioning;

namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsGpu;

[SupportedOSPlatform("windows")]
public sealed class WindowsGpuPerformanceCounterSource : IWindowsGpuPerformanceCounterSource
{
    private const string EngineClassName = "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine";
    private const string ProcessMemoryClassName = "Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(1);

    public Task<WindowsGpuPerformanceCounterAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var availability = new WindowsGpuPerformanceCounterAvailability
            {
                SupportsEngineUtilization = ClassExists(EngineClassName),
                SupportsProcessMemory = ClassExists(ProcessMemoryClassName)
            };
            cancellationToken.ThrowIfCancellationRequested();
            return availability;
        }, cancellationToken);

    public Task<GpuProcessPerformanceSnapshot> CollectProcessAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usage = QueryUsagePercent(processId);
            var (dedicatedBytes, sharedBytes) = QueryProcessMemory(processId);
            cancellationToken.ThrowIfCancellationRequested();
            return new GpuProcessPerformanceSnapshot
            {
                UsagePercent = usage,
                DedicatedMemoryBytes = dedicatedBytes,
                SharedMemoryBytes = sharedBytes
            };
        }, cancellationToken);
    }

    private static bool ClassExists(string className)
    {
        using var searcher = CreateSearcher(
            $"SELECT __CLASS FROM meta_class WHERE __CLASS = '{className}'");
        using var results = searcher.Get();
        foreach (ManagementBaseObject result in results)
        {
            using (result)
            {
                return true;
            }
        }

        return false;
    }

    private static double? QueryUsagePercent(int processId)
    {
        try
        {
            using var searcher = CreateSearcher(
                $"SELECT Name, UtilizationPercentage FROM {EngineClassName} WHERE Name LIKE 'pid_{processId}_%'");
            using var results = searcher.Get();
            var values = new List<double>();
            foreach (ManagementObject result in results)
            {
                using (result)
                {
                    if (ReadDouble(result, "UtilizationPercentage") is { } value)
                    {
                        values.Add(value);
                    }
                }
            }

            return values.Count == 0 ? null : Math.Clamp(values.Max(), 0, 100);
        }
        catch (ManagementException exception) when (IsMissingCounterClass(exception))
        {
            return null;
        }
    }

    private static (double? DedicatedBytes, double? SharedBytes) QueryProcessMemory(int processId)
    {
        try
        {
            using var searcher = CreateSearcher(
                $"SELECT Name, DedicatedUsage, SharedUsage FROM {ProcessMemoryClassName} WHERE Name LIKE 'pid_{processId}_%'");
            using var results = searcher.Get();
            var found = false;
            double dedicated = 0;
            double shared = 0;
            foreach (ManagementObject result in results)
            {
                using (result)
                {
                    found = true;
                    dedicated += ReadDouble(result, "DedicatedUsage") ?? 0;
                    shared += ReadDouble(result, "SharedUsage") ?? 0;
                }
            }

            return found ? (dedicated, shared) : (null, null);
        }
        catch (ManagementException exception) when (IsMissingCounterClass(exception))
        {
            return (null, null);
        }
    }

    private static bool IsMissingCounterClass(ManagementException exception) =>
        exception.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.NotFound;

    private static ManagementObjectSearcher CreateSearcher(string query) =>
        new(
            new ManagementScope("root\\cimv2"),
            new ObjectQuery(query),
            new System.Management.EnumerationOptions
            {
                ReturnImmediately = false,
                Rewindable = false,
                Timeout = QueryTimeout
            });

    private static double? ReadDouble(ManagementBaseObject instance, string propertyName)
    {
        var value = instance[propertyName];
        if (value is null)
        {
            return null;
        }

        try
        {
            var converted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(converted) && converted >= 0 ? converted : null;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
