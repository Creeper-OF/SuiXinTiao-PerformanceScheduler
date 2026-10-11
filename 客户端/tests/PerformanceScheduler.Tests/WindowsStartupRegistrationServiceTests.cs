using PerformanceScheduler.App.Settings;

namespace PerformanceScheduler.Tests;

public sealed class WindowsStartupRegistrationServiceTests
{
    [Fact]
    public void FormatCommandLine_WithoutArgument_EnclosesPathInQuotes()
    {
        var result = WindowsStartupRegistrationService.FormatCommandLine(@"C:\Program Files\PerformanceScheduler\app.exe");
        Assert.Equal("\"C:\\Program Files\\PerformanceScheduler\\app.exe\"", result);
    }

    [Fact]
    public void FormatCommandLine_WithSilentArgument_AppendsArgumentWithSpace()
    {
        var result = WindowsStartupRegistrationService.FormatCommandLine(
            @"C:\Program Files\PerformanceScheduler\app.exe",
            "--silent");
        Assert.Equal("\"C:\\Program Files\\PerformanceScheduler\\app.exe\" --silent", result);
    }

    [Fact]
    public void FormatCommandLine_WithWhitespaceArgument_TrimsCorrectly()
    {
        var result = WindowsStartupRegistrationService.FormatCommandLine(
            @"C:\app.exe",
            "  --silent  ");
        Assert.Equal("\"C:\\app.exe\" --silent", result);
    }

    [Fact]
    public void FormatCommandLine_WithEmptyArgument_EnclosesPathOnly()
    {
        var result = WindowsStartupRegistrationService.FormatCommandLine(@"C:\app.exe", "   ");
        Assert.Equal("\"C:\\app.exe\"", result);
    }
}
