using System.Globalization;
using System.Management;
using System.Runtime.Versioning;

namespace PerformanceScheduler.Infrastructure.Telemetry.WindowsSystem;

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemPerformanceCounterSource : IWindowsSystemPerformanceCounterSource
{
    private const string ProcessorInformationClassName =
        "Win32_PerfFormattedData_Counters_ProcessorInformation";
    private const string ProcessorClassName =
        "Win32_PerfFormattedData_PerfOS_Processor";
    private const string ThermalZoneClassName =
        "Win32_PerfFormattedData_Counters_ThermalZoneInformation";
    private const string GpuEngineClassName =
        "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine";
    private const string GpuAdapterMemoryClassName =
        "Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(1);

    public Task<WindowsSystemPerformanceCounterAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var availability = new WindowsSystemPerformanceCounterAvailability
            {
                SupportsCpuFrequency = ClassExists(ProcessorInformationClassName),
                SupportsCpuUtilization = ClassExists(ProcessorClassName),
                SupportsThermalZoneTemperature = ClassExists(ThermalZoneClassName),
                SupportsGpuUtilization = ClassExists(GpuEngineClassName),
                SupportsGpuMemory = ClassExists(GpuAdapterMemoryClassName)
            };
            cancellationToken.ThrowIfCancellationRequested();
            return availability;
        }, cancellationToken);

    public Task<WindowsSystemPerformanceSnapshot> CollectAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new WindowsSystemPerformanceSnapshot
            {
                CpuFrequencyMegahertz = QueryCpuFrequencyMegahertz(),
                CpuUsagePercent = QueryCpuUsagePercent(),
                ThermalZoneTemperatureCelsius = QueryThermalZoneTemperatureCelsius(),
                GpuUsagePercent = QueryGpuUsagePercent()
            };
            var gpuMemory = QueryGpuMemoryBytes();
            snapshot = snapshot with
            {
                GpuDedicatedMemoryBytes = gpuMemory.DedicatedBytes,
                GpuSharedMemoryBytes = gpuMemory.SharedBytes
            };
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }, cancellationToken);

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

    private static double? QueryCpuFrequencyMegahertz()
    {
        try
        {
            using var searcher = CreateSearcher(
                $"SELECT Name, ActualFrequency, ProcessorFrequency FROM {ProcessorInformationClassName}");
            using var results = searcher.Get();
            var measurements = new List<(string Name, double? Actual, double? Nominal)>();
            foreach (ManagementObject result in results)
            {
                using (result)
                {
                    measurements.Add((
                        result["Name"] as string ?? string.Empty,
                        ReadNonNegativeDouble(result, "ActualFrequency"),
                        ReadNonNegativeDouble(result, "ProcessorFrequency")));
                }
            }

            var aggregate = measurements.FirstOrDefault(item =>
                string.Equals(item.Name, "_Total", StringComparison.OrdinalIgnoreCase));
            if (aggregate == default)
            {
                aggregate = measurements.FirstOrDefault(item =>
                    item.Name.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase));
            }

            var value = aggregate != default
                ? aggregate.Actual ?? aggregate.Nominal
                : Average(measurements.Select(item => item.Actual ?? item.Nominal));
            return value is { } frequency && frequency > 0 ? frequency : null;
        }
        catch (ManagementException exception) when (IsMissingCounterClass(exception))
        {
            return null;
        }
    }

    private static double? QueryThermalZoneTemperatureCelsius()
    {
        try
        {
            using var searcher = CreateSearcher(
                $"SELECT Temperature, HighPrecisionTemperature FROM {ThermalZoneClassName}");
            using var results = searcher.Get();
            var temperatures = new List<double>();
            foreach (ManagementObject result in results)
            {
                using (result)
                {
                    var highPrecisionKelvin = ReadNonNegativeDouble(result, "HighPrecisionTemperature");
                    var kelvin = highPrecisionKelvin is > 0
                        ? highPrecisionKelvin.Value / 10d
                        : ReadNonNegativeDouble(result, "Temperature");
                    if (kelvin is { } value)
                    {
                        var celsius = value - 273.15d;
                        if (celsius is >= -40 and <= 150)
                        {
                            temperatures.Add(celsius);
                        }
                    }
                }
            }

            return temperatures.Count == 0 ? null : temperatures.Max();
        }
        catch (ManagementException exception) when (IsMissingCounterClass(exception))
        {
            return null;
        }
    }

    private static double? QueryCpuUsagePercent()
    {
        try
        {
            using var searcher = CreateSearcher(
                $"SELECT Name, PercentProcessorTime FROM {ProcessorClassName}");
            using var results = searcher.Get();
            var measurements = new List<(string Name, double? Usage)>();
            foreach (ManagementObject result in results)
            {
                using (result)
                {
                    measurements.Add((
                        result["Name"] as string ?? string.Empty,
                        ReadNonNegativeDouble(result, "PercentProcessorTime")));
                }
            }

            var aggregate = measurements.FirstOrDefault(item =>
                string.Equals(item.Name, "_Total", StringComparison.OrdinalIgnoreCase));
            if (aggregate == default)
            {
                aggregate = measurements.FirstOrDefault(item =>
                    item.Name.EndsWith(",_Total", StringComparison.OrdinalIgnoreCase));
            }

            var value = aggregate != default
                ? aggregate.Usage
                : Average(measurements.Select(item => item.Usage));
            return value is { } usage ? Math.Clamp(usage, 0, 100) : null;
        }
        catch (ManagementException exception) when (IsMissingCounterClass(exception))
        {
            return null;
        }
    }

    private static double? QueryGpuUsagePercent()
    {
        try
        {
            using var searcher = CreateSearcher(
                $"SELECT UtilizationPercentage FROM {GpuEngineClassName}");
            using var results = searcher.Get();
            var values = new List<double>();
            foreach (ManagementObject result in results)
            {
                using (result)
                {
                    if (ReadNonNegativeDouble(result, "UtilizationPercentage") is { } value)
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

    private static (double? DedicatedBytes, double? SharedBytes) QueryGpuMemoryBytes()
    {
        try
        {
            using var searcher = CreateSearcher(
                $"SELECT DedicatedUsage, SharedUsage FROM {GpuAdapterMemoryClassName}");
            using var results = searcher.Get();
            var found = false;
            double dedicated = 0;
            double shared = 0;
            foreach (ManagementObject result in results)
            {
                using (result)
                {
                    found = true;
                    dedicated += ReadNonNegativeDouble(result, "DedicatedUsage") ?? 0;
                    shared += ReadNonNegativeDouble(result, "SharedUsage") ?? 0;
                }
            }

            return found ? (dedicated, shared) : (null, null);
        }
        catch (ManagementException exception) when (IsMissingCounterClass(exception))
        {
            return (null, null);
        }
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var validValues = values.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        return validValues.Length == 0 ? null : validValues.Average();
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

    private static double? ReadNonNegativeDouble(ManagementBaseObject instance, string propertyName)
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
