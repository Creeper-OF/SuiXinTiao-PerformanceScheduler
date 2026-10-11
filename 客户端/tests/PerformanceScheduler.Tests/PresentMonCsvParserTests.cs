using PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

namespace PerformanceScheduler.Tests;

public sealed class PresentMonCsvParserTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ParseAsync_FiltersProcessAndCalculatesFrameStatistics()
    {
        var path = await WriteCsvAsync(
            "Application,ProcessID,MsBetweenPresents\n" +
            "\"Game, Client\",42,10\n" +
            "Game.exe,42,20\n" +
            "Other.exe,999,1\n" +
            "Game.exe,42,30\n" +
            "Game.exe,42,40\n");

        var result = await new PresentMonCsvParser().ParseAsync(path, 42);

        Assert.NotNull(result);
        Assert.Equal(4, result.SampleCount);
        Assert.Equal(40, result.AverageFramesPerSecond, precision: 6);
        Assert.Equal(25, result.AverageFrameTimeMilliseconds, precision: 6);
        Assert.Equal(38.5, result.FrameTimeP95Milliseconds, precision: 6);
        Assert.Equal(39.7, result.FrameTimeP99Milliseconds, precision: 6);
    }

    [Fact]
    public async Task ParseAsync_WithNoSupportedFrameTimeColumn_Throws()
    {
        var path = await WriteCsvAsync("Application,ProcessID,Unknown\nGame.exe,42,10\n");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new PresentMonCsvParser().ParseAsync(path, 42));
    }

    [Fact]
    public void ParseCsvLine_HandlesEscapedQuotesAndCommas()
    {
        var fields = PresentMonCsvParser.ParseCsvLine("\"Game, \"\"Client\"\"\",42,16.7");

        Assert.Equal(["Game, \"Client\"", "42", "16.7"], fields);
    }

    private async Task<string> WriteCsvAsync(string content)
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
