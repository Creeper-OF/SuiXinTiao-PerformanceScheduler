using PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

namespace PerformanceScheduler.Tests;

public sealed class PresentMonExecutableLocatorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindExecutable_PrefersExplicitEnvironmentPath()
    {
        var configuredPath = CreateFile(Path.Combine(_tempDirectory, "configured", "PresentMon.exe"));
        CreateFile(Path.Combine(_tempDirectory, "tools", "PresentMon", "PresentMon.exe"));
        var locator = new PresentMonExecutableLocator(
            _tempDirectory,
            name => name == PresentMonExecutableLocator.EnvironmentVariableName ? configuredPath : null);

        Assert.Equal(Path.GetFullPath(configuredPath), locator.FindExecutable());
    }

    [Fact]
    public void FindExecutable_FindsVersionedBinaryInTrustedToolsDirectory()
    {
        var expected = CreateFile(Path.Combine(
            _tempDirectory,
            "tools",
            "PresentMon",
            "PresentMon-2.4.1-x64.exe"));
        var locator = new PresentMonExecutableLocator(_tempDirectory, _ => null);

        Assert.Equal(Path.GetFullPath(expected), locator.FindExecutable());
    }

    [Fact]
    public void FindExecutable_DoesNotAcceptNonExecutableConfiguration()
    {
        var configuredPath = CreateFile(Path.Combine(_tempDirectory, "PresentMon.txt"));
        var locator = new PresentMonExecutableLocator(_tempDirectory, _ => configuredPath);

        Assert.Null(locator.FindExecutable());
    }

    [Fact]
    public void FindExecutable_WithInvalidConfiguredPath_DegradesToUnavailable()
    {
        var locator = new PresentMonExecutableLocator(_tempDirectory, _ => "bad\0path.exe");

        Assert.Null(locator.FindExecutable());
    }

    [Theory]
    [InlineData("PresentMonService.exe")]
    [InlineData("PresentMonCli.exe")]
    [InlineData("NotPresentMon.exe")]
    public void FindExecutable_DoesNotTreatOtherExecutablesAsConsole(string fileName)
    {
        var configuredPath = CreateFile(Path.Combine(_tempDirectory, fileName));
        var locator = new PresentMonExecutableLocator(_tempDirectory, _ => configuredPath);

        Assert.Null(locator.FindExecutable());
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
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
