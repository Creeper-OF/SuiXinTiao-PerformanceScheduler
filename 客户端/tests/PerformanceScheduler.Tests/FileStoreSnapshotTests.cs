using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Tests;

public sealed class FileStoreSnapshotTests : IDisposable
{
    private readonly string _directory;

    public FileStoreSnapshotTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "PerformanceScheduler.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task RestoreDirectoryAsync_ValidatesSnapshotBeforeDeletingLiveFiles()
    {
        var currentPath = Path.Combine(_directory, "current.json");
        await File.WriteAllTextAsync(currentPath, "keep me");
        var snapshot = new ConfigurationStoreSnapshot
        {
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["..\\outside.json"] = "should not be written"u8.ToArray()
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FileStoreSnapshot.RestoreDirectoryAsync(_directory, snapshot));

        Assert.Equal("keep me", await File.ReadAllTextAsync(currentPath));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(_directory)!.FullName, "outside.json")));
    }

    [Fact]
    public async Task RestoreDirectoryAsync_RejectsCancellationBeforeDeletingLiveFiles()
    {
        var currentPath = Path.Combine(_directory, "current.json");
        await File.WriteAllTextAsync(currentPath, "keep me");
        var snapshot = new ConfigurationStoreSnapshot
        {
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["restored.json"] = "restored"u8.ToArray()
            }
        };
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FileStoreSnapshot.RestoreDirectoryAsync(_directory, snapshot, cancellationSource.Token));

        Assert.Equal("keep me", await File.ReadAllTextAsync(currentPath));
    }

    [Fact]
    public async Task RestoreFileAsync_RejectsNullFileContents()
    {
        var filePath = Path.Combine(_directory, "settings.json");
        var snapshot = new ConfigurationStoreSnapshot
        {
            Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = null!
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FileStoreSnapshot.RestoreFileAsync(filePath, snapshot));

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task RestoreFileAsync_RejectsCancellationBeforeDeletingLiveFile()
    {
        var filePath = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(filePath, "keep me");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FileStoreSnapshot.RestoreFileAsync(
                filePath,
                new ConfigurationStoreSnapshot(),
                cancellationSource.Token));

        Assert.Equal("keep me", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task CaptureDirectoryAsync_RejectsFileAtDirectoryStorePath()
    {
        var storePath = Path.Combine(_directory, "profiles");
        await File.WriteAllTextAsync(storePath, "not a directory");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FileStoreSnapshot.CaptureDirectoryAsync(storePath));
    }

    [Fact]
    public async Task RestoreFileAsync_RejectsDirectoryAtFileStorePath()
    {
        var storePath = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(storePath);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FileStoreSnapshot.RestoreFileAsync(
                storePath,
                new ConfigurationStoreSnapshot()));
    }

    [Fact]
    public async Task EnumerateTopLevelJsonFiles_DoesNotIncludeNestedHistoryFiles()
    {
        var currentPath = Path.Combine(_directory, "current.json");
        var historyDirectory = Path.Combine(_directory, ".history");
        Directory.CreateDirectory(historyDirectory);
        await File.WriteAllTextAsync(currentPath, "current");
        await File.WriteAllTextAsync(Path.Combine(historyDirectory, "revision.json"), "history");

        var files = FileStoreSnapshot.EnumerateTopLevelJsonFiles(_directory);

        var file = Assert.Single(files);
        Assert.Equal(currentPath, file, ignoreCase: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
