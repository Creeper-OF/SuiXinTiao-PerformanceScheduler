using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.IntelLevelZero;

[SupportedOSPlatform("windows")]
public sealed class IntelGpuSource : IIntelGpuSource
{
    private static readonly string[] DriverLibraryCandidates =
    [
        "ze_loader.dll",
        "igfxcmrt64.dll"
    ];

    public Task<IntelGpuAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindDriverLibrary(out var libraryPath))
            {
                return new IntelGpuAvailability
                {
                    IsAvailable = false,
                    FailureReason = "Intel Level Zero libraries (ze_loader.dll / igfxcmrt64.dll) not found."
                };
            }

            if (!NativeLibrary.TryLoad(libraryPath, out var libraryHandle))
            {
                return new IntelGpuAvailability
                {
                    IsAvailable = false,
                    FailureReason = $"Failed to load Intel driver library from '{libraryPath}'."
                };
            }

            try
            {
                return new IntelGpuAvailability
                {
                    IsAvailable = true,
                    DeviceCount = 1,
                    SupportsClockLocking = true
                };
            }
            finally
            {
                NativeLibrary.Free(libraryHandle);
            }
        }, cancellationToken);

    public Task<IntelGpuSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new IntelGpuSnapshot());

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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "oneAPI")
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
}
