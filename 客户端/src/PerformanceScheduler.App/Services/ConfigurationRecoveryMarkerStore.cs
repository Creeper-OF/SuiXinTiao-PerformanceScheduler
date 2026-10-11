using System.IO;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.App.Services;

/// <summary>
/// Persists the fact that a configuration store needs manual recovery.
/// Presence of the marker is intentionally enough; its contents are only
/// diagnostic and must never be used as a scheduling instruction.
/// </summary>
internal sealed class ConfigurationRecoveryMarkerStore
{
    private const string MarkerFileName = "configuration-recovery-required.marker";
    private readonly string _markerPath;

    public ConfigurationRecoveryMarkerStore(string runtimeDirectory)
    {
        _markerPath = Path.Combine(Path.GetFullPath(runtimeDirectory), MarkerFileName);
    }

    public bool IsMarked
    {
        get
        {
            try
            {
                FileStoreSnapshot.EnsureSafeFilePath(_markerPath);
                return File.Exists(_markerPath);
            }
            catch (Exception)
            {
                // An unreadable marker is safer than silently allowing a new
                // scheduling session to start.
                return true;
            }
        }
    }

    public void Mark()
    {
        AtomicFile.WriteAllText(
            _markerPath,
            $"Configuration recovery required.{Environment.NewLine}Marked at {DateTimeOffset.UtcNow:O}.{Environment.NewLine}");
    }

    public bool TryClear(out Exception? failure)
    {
        try
        {
            FileStoreSnapshot.EnsureSafeFilePath(_markerPath);
            if (File.Exists(_markerPath))
            {
                // Re-check immediately before deleting the marker so a
                // replaced reparse point cannot redirect cleanup.
                FileStoreSnapshot.EnsureSafeFilePath(_markerPath);
                File.Delete(_markerPath);
            }

            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception;
            return false;
        }
    }
}
