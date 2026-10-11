using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;

[SupportedOSPlatform("windows")]
public sealed class NvidiaNvmlSource : INvidiaNvmlSource
{
    private const string LibraryName = "nvml.dll";
    private const int NvmlSuccess = 0;
    private const uint GpuTemperatureSensor = 0;
    private const uint GraphicsClock = 0;

    public Task<NvidiaNvmlAvailability> GetAvailabilityAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NvmlApi.TryCreate(out var api))
            {
                return new NvidiaNvmlAvailability
                {
                    FailureReason = "nvml.dll is not available or does not expose the required read-only APIs."
                };
            }

            using (api)
            {
                var initResult = api.Init();
                if (initResult != NvmlSuccess)
                {
                    return Unavailable($"NVML initialization returned {initResult}.");
                }

                try
                {
                    var countResult = api.GetDeviceCount(out var deviceCount);
                    return countResult == NvmlSuccess && deviceCount > 0
                        ? new NvidiaNvmlAvailability
                        {
                            IsAvailable = true,
                            DeviceCount = checked((int)deviceCount),
                            SupportsClockLocking = api.SetGpuLockedClocks != null && api.ResetGpuLockedClocks != null
                        }
                        : Unavailable($"NVML device enumeration returned {countResult}.");
                }
                finally
                {
                    api.Shutdown();
                }
            }
        }, cancellationToken);

    public Task<NvidiaNvmlSnapshot> CollectAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NvmlApi.TryCreate(out var api))
            {
                throw new InvalidOperationException(
                    "nvml.dll is not available or does not expose the required read-only APIs.");
            }

            using (api)
            {
                var initResult = api.Init();
                if (initResult != NvmlSuccess)
                {
                    throw new InvalidOperationException($"NVML initialization returned {initResult}.");
                }

                try
                {
                    var countResult = api.GetDeviceCount(out var deviceCount);
                    if (countResult != NvmlSuccess)
                    {
                        throw new InvalidOperationException(
                            $"NVML device enumeration returned {countResult}.");
                    }

                    var usages = new List<double>();
                    var temperatures = new List<double>();
                    var powers = new List<double>();
                    var clocks = new List<double>();
                    for (uint index = 0; index < deviceCount; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (api.GetHandleByIndex(index, out var device) != NvmlSuccess)
                        {
                            continue;
                        }

                        if (api.GetUtilization(device, out var utilization) == NvmlSuccess &&
                            utilization.Gpu <= 100)
                        {
                            usages.Add(utilization.Gpu);
                        }

                        if (api.GetTemperature(device, GpuTemperatureSensor, out var temperature) == NvmlSuccess &&
                            temperature <= 150)
                        {
                            temperatures.Add(temperature);
                        }

                        if (api.GetPowerUsage(device, out var powerMilliwatts) == NvmlSuccess)
                        {
                            powers.Add(powerMilliwatts / 1000d);
                        }

                        if (api.GetClockInfo(device, GraphicsClock, out var clock) == NvmlSuccess &&
                            clock > 0)
                        {
                            clocks.Add(clock);
                        }
                    }

                    return new NvidiaNvmlSnapshot
                    {
                        UsagePercent = usages.Count == 0 ? null : usages.Max(),
                        TemperatureCelsius = temperatures.Count == 0 ? null : temperatures.Max(),
                        PowerWatts = powers.Count == 0 ? null : powers.Sum(),
                        GraphicsClockMegahertz = clocks.Count == 0 ? null : clocks.Max()
                    };
                }
                finally
                {
                    api.Shutdown();
                }
            }
        }, cancellationToken);

    public Task<bool> TrySetLockedClocksAsync(
        uint minGpuClockMHz,
        uint maxGpuClockMHz,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NvmlApi.TryCreate(out var api))
            {
                return false;
            }

            using (api)
            {
                if (api.SetGpuLockedClocks == null)
                {
                    return false;
                }

                var initResult = api.Init();
                if (initResult != NvmlSuccess)
                {
                    return false;
                }

                try
                {
                    if (api.GetDeviceCount(out var deviceCount) != NvmlSuccess || deviceCount == 0)
                    {
                        return false;
                    }

                    return NvmlDeviceCommandBatch.Execute(deviceCount, index =>
                        api.GetHandleByIndex(index, out var device) == NvmlSuccess &&
                        api.SetGpuLockedClocks(device, minGpuClockMHz, maxGpuClockMHz) == NvmlSuccess,
                        cancellationToken);
                }
                finally
                {
                    api.Shutdown();
                }
            }
        }, cancellationToken);

    public Task<bool> TryResetLockedClocksAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NvmlApi.TryCreate(out var api))
            {
                return false;
            }

            using (api)
            {
                if (api.ResetGpuLockedClocks == null)
                {
                    return false;
                }

                var initResult = api.Init();
                if (initResult != NvmlSuccess)
                {
                    return false;
                }

                try
                {
                    if (api.GetDeviceCount(out var deviceCount) != NvmlSuccess || deviceCount == 0)
                    {
                        return false;
                    }

                    return NvmlDeviceCommandBatch.Execute(deviceCount, index =>
                        api.GetHandleByIndex(index, out var device) == NvmlSuccess &&
                        api.ResetGpuLockedClocks(device) == NvmlSuccess,
                        cancellationToken);
                }
                finally
                {
                    api.Shutdown();
                }
            }
        }, cancellationToken);

    private static NvidiaNvmlAvailability Unavailable(string reason) =>
        new() { FailureReason = reason };

    private sealed class NvmlApi : IDisposable
    {
        private readonly IntPtr _library;
        private readonly NvmlShutdownDelegate _shutdown;

        private NvmlApi(
            IntPtr library,
            NvmlInitDelegate init,
            NvmlShutdownDelegate shutdown,
            NvmlGetDeviceCountDelegate getDeviceCount,
            NvmlGetHandleByIndexDelegate getHandleByIndex,
            NvmlGetUtilizationDelegate getUtilization,
            NvmlGetTemperatureDelegate getTemperature,
            NvmlGetPowerUsageDelegate getPowerUsage,
            NvmlGetClockInfoDelegate getClockInfo,
            NvmlSetGpuLockedClocksDelegate? setGpuLockedClocks,
            NvmlResetGpuLockedClocksDelegate? resetGpuLockedClocks)
        {
            _library = library;
            Init = init;
            _shutdown = shutdown;
            GetDeviceCount = getDeviceCount;
            GetHandleByIndex = getHandleByIndex;
            GetUtilization = getUtilization;
            GetTemperature = getTemperature;
            GetPowerUsage = getPowerUsage;
            GetClockInfo = getClockInfo;
            SetGpuLockedClocks = setGpuLockedClocks;
            ResetGpuLockedClocks = resetGpuLockedClocks;
        }

        public NvmlInitDelegate Init { get; }

        public NvmlGetDeviceCountDelegate GetDeviceCount { get; }

        public NvmlGetHandleByIndexDelegate GetHandleByIndex { get; }

        public NvmlGetUtilizationDelegate GetUtilization { get; }

        public NvmlGetTemperatureDelegate GetTemperature { get; }

        public NvmlGetPowerUsageDelegate GetPowerUsage { get; }

        public NvmlGetClockInfoDelegate GetClockInfo { get; }

        public NvmlSetGpuLockedClocksDelegate? SetGpuLockedClocks { get; }

        public NvmlResetGpuLockedClocksDelegate? ResetGpuLockedClocks { get; }

        public static bool TryCreate(out NvmlApi api)
        {
            api = null!;
            if (!TryLoadNvml(out var library))
            {
                return false;
            }

            try
            {
                api = new NvmlApi(
                    library,
                    GetDelegate<NvmlInitDelegate>(library, "nvmlInit_v2"),
                    GetDelegate<NvmlShutdownDelegate>(library, "nvmlShutdown"),
                    GetDelegate<NvmlGetDeviceCountDelegate>(library, "nvmlDeviceGetCount_v2"),
                    GetDelegate<NvmlGetHandleByIndexDelegate>(library, "nvmlDeviceGetHandleByIndex_v2"),
                    GetDelegate<NvmlGetUtilizationDelegate>(library, "nvmlDeviceGetUtilizationRates"),
                    GetDelegate<NvmlGetTemperatureDelegate>(library, "nvmlDeviceGetTemperature"),
                    GetDelegate<NvmlGetPowerUsageDelegate>(library, "nvmlDeviceGetPowerUsage"),
                    GetDelegate<NvmlGetClockInfoDelegate>(library, "nvmlDeviceGetClockInfo"),
                    TryGetDelegate<NvmlSetGpuLockedClocksDelegate>(library, "nvmlDeviceSetGpuLockedClocks"),
                    TryGetDelegate<NvmlResetGpuLockedClocksDelegate>(library, "nvmlDeviceResetGpuLockedClocks"));
                return true;
            }
            catch
            {
                NativeLibrary.Free(library);
                return false;
            }
        }

        private static bool TryLoadNvml(out IntPtr library)
        {
            library = IntPtr.Zero;
            var candidates = new[]
            {
                Path.Combine(Environment.SystemDirectory, LibraryName),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation",
                    "NVSMI",
                    LibraryName)
            };

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out library))
                {
                    return true;
                }
            }

            return false;
        }

        public void Shutdown() => _shutdown();

        public void Dispose() => NativeLibrary.Free(_library);

        private static TDelegate GetDelegate<TDelegate>(IntPtr library, string name)
            where TDelegate : Delegate =>
            Marshal.GetDelegateForFunctionPointer<TDelegate>(NativeLibrary.GetExport(library, name));

        private static TDelegate? TryGetDelegate<TDelegate>(IntPtr library, string name)
            where TDelegate : Delegate =>
            NativeLibrary.TryGetExport(library, name, out var address)
                ? Marshal.GetDelegateForFunctionPointer<TDelegate>(address)
                : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetDeviceCountDelegate(out uint deviceCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetHandleByIndexDelegate(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetUtilizationDelegate(IntPtr device, out NvmlUtilization utilization);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetTemperatureDelegate(
        IntPtr device,
        uint sensorType,
        out uint temperature);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetPowerUsageDelegate(IntPtr device, out uint powerMilliwatts);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetClockInfoDelegate(
        IntPtr device,
        uint clockType,
        out uint clockMegahertz);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSetGpuLockedClocksDelegate(
        IntPtr device,
        uint minGpuClockMHz,
        uint maxGpuClockMHz);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlResetGpuLockedClocksDelegate(IntPtr device);
}
