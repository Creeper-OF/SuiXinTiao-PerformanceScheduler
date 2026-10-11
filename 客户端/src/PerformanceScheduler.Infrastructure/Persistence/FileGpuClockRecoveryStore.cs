using System.Text.Json;
using PerformanceScheduler.Infrastructure.Hardware.Gpu;

namespace PerformanceScheduler.Infrastructure.Persistence;

public sealed class FileGpuClockRecoveryStore(string statePath) : IGpuClockRecoveryStore
{
    private readonly string _statePath = Path.GetFullPath(statePath);

    public async Task<bool> IsPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStoreSnapshot.EnsureSafeFilePath(_statePath);
        if (!File.Exists(_statePath))
        {
            return false;
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
                !root.TryGetProperty("resetRequired", out var pending) ||
                pending.ValueKind != JsonValueKind.True)
            {
                throw new InvalidDataException("GPU clock recovery state is invalid or uses an unsupported schema.");
            }

            return true;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GPU clock recovery state could not be parsed; the original file was preserved.", exception);
        }
    }

    public async Task MarkPendingAsync(CancellationToken cancellationToken = default)
    {
        if (await IsPendingAsync(cancellationToken))
        {
            return;
        }

        await AtomicFile.WriteJsonAsync(
            _statePath,
            new { schemaVersion = 1, resetRequired = true },
            new JsonSerializerOptions(),
            cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsPendingAsync(cancellationToken))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileStoreSnapshot.EnsureSafeFilePath(_statePath);
        File.Delete(_statePath);
    }
}
