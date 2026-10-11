using PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

namespace PerformanceScheduler.Tests;

public sealed class PresentMonProcessCaptureRunnerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateStartInfo_UsesArgumentListAndDoesNotStopExistingSessions()
    {
        Directory.CreateDirectory(_tempDirectory);
        var executablePath = Path.Combine(_tempDirectory, "Present Mon.exe");
        File.WriteAllText(executablePath, string.Empty);
        var outputPath = Path.Combine(_tempDirectory, "output with spaces", "frames.csv");

        var startInfo = PresentMonProcessCaptureRunner.CreateStartInfo(
            executablePath,
            new PresentMonCaptureRequest
            {
                ProcessId = 42,
                Duration = TimeSpan.FromSeconds(1),
                OutputFilePath = outputPath,
                SessionName = "SuiXinTiao-test"
            });

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(Path.GetFullPath(executablePath), startInfo.FileName);
        Assert.Contains("--process_id", startInfo.ArgumentList);
        Assert.Contains("42", startInfo.ArgumentList);
        Assert.Contains(Path.GetFullPath(outputPath), startInfo.ArgumentList);
        Assert.Contains("--terminate_after_timed", startInfo.ArgumentList);
        Assert.Contains("SuiXinTiao-test", startInfo.ArgumentList);
        Assert.DoesNotContain("--stop_existing_session", startInfo.ArgumentList);
        Assert.DoesNotContain("--terminate_existing_session", startInfo.ArgumentList);
        Assert.DoesNotContain("--restart_as_admin", startInfo.ArgumentList);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
