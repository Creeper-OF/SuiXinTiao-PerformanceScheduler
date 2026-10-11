using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    public AtomicFileTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void WriteAllText_RejectsDirectoryDestinationBeforeWriting()
    {
        var destinationPath = Path.Combine(_tempDirectory, "settings.json");
        Directory.CreateDirectory(destinationPath);

        Assert.Throws<InvalidDataException>(() => AtomicFile.WriteAllText(destinationPath, "should not write"));

        Assert.True(Directory.Exists(destinationPath));
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task WriteBytesAsync_CreatesParentAndReplacesRegularFile()
    {
        var destinationPath = Path.Combine(_tempDirectory, "nested", "state.json");

        await AtomicFile.WriteBytesAsync(destinationPath, "first"u8.ToArray());
        await AtomicFile.WriteBytesAsync(destinationPath, "second"u8.ToArray());

        Assert.Equal("second", await File.ReadAllTextAsync(destinationPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destinationPath)!, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
