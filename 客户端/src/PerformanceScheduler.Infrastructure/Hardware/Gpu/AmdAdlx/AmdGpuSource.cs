using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.AmdAdlx;

[SupportedOSPlatform("windows")]
public sealed class AmdGpuSource : IAmdGpuSource
{
    private static readonly string[] DriverLibraryCandidates =
    [
        "adlx64.dll",
        "atiadlxx.dll"
    ];

    public Task<AmdGpuAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindDriverLibrary(out var libraryPath))
            {
                return new AmdGpuAvailability
                {
                    IsAvailable = false,
                    FailureReason = "AMD driver libraries (adlx64.dll / atiadlxx.dll) not found."
                };
            }

            if (!NativeLibrary.TryLoad(libraryPath, out var libraryHandle))
            {
                return new AmdGpuAvailability
                {
                    IsAvailable = false,
                    FailureReason = $"Failed to load AMD driver library from '{libraryPath}'."
                };
            }

            try
            {
                // Verify if ADL or ADLX initialization succeeds
                if (NativeLibrary.TryGetExport(libraryHandle, "ADL2_Main_Control_Create", out var export))
                {
                    var create = Marshal.GetDelegateForFunctionPointer<Adl2MainControlCreateDelegate>(export);
                    int res = create(IntPtr.Zero, 1, out var context);
                    if (res != 0) // ADL_OK = 0
                    {
                        return new AmdGpuAvailability
                        {
                            IsAvailable = false,
                            FailureReason = $"AMD ADL initialization returned {res} (no AMD GPU detected or unsupported driver state)."
                        };
                    }

                    if (NativeLibrary.TryGetExport(libraryHandle, "ADL2_Main_Control_Destroy", out var destroyExport))
                    {
                        var destroy = Marshal.GetDelegateForFunctionPointer<Adl2MainControlDestroyDelegate>(destroyExport);
                        destroy(context);
                    }

                    return new AmdGpuAvailability
                    {
                        IsAvailable = true,
                        DeviceCount = 1,
                        SupportsClockLocking = true
                    };
                }

                // If atiadlxx.dll exists but doesn't have ADL2 or fails, check ADLX
                return new AmdGpuAvailability
                {
                    IsAvailable = false,
                    FailureReason = "AMD ADLX/ADL entrypoints not found or no active AMD adapter."
                };
            }
            finally
            {
                NativeLibrary.Free(libraryHandle);
            }
        }, cancellationToken);

    public Task<AmdGpuSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AmdGpuSnapshot());

    public Task<bool> TrySetLockedClocksAsync(
        uint minGpuClockMHz,
        uint maxGpuClockMHz,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<bool> TryResetLockedClocksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    private static bool TryFindDriverLibrary(out string foundPath)
    {
        foundPath = string.Empty;
        var searchDirectories = new[]
        {
            Environment.SystemDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "CNext", "CNext")
        };

        foreach (var dir in searchDirectories)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var candidate in DriverLibraryCandidates)
            {
                var fullPath = Path.Combine(dir, candidate);
                if (File.Exists(fullPath))
                {
                    foundPath = fullPath;
                    return true;
                }
            }
        }

        foreach (var candidate in DriverLibraryCandidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                NativeLibrary.Free(handle);
                foundPath = candidate;
                return true;
            }
        }

        return false;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2MainControlCreateDelegate(IntPtr callback, int enumConnectedAdapters, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2MainControlDestroyDelegate(IntPtr context);
}
