using System.Diagnostics;
using System.Globalization;

namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public sealed class PresentMonProcessCaptureRunner : IPresentMonCaptureRunner
{
    public async Task<PresentMonCaptureResult> CaptureAsync(
        string executablePath,
        PresentMonCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(executablePath, request);
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("PresentMon process could not be started.");
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new PresentMonCaptureResult
            {
                ExitCode = process.ExitCode,
                StandardError = await standardErrorTask
            };
        }
        catch
        {
            TryStopOwnedProcess(process);
            try
            {
                await standardErrorTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Preserve the original cancellation or process failure.
            }
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        PresentMonCaptureRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("PresentMon executable was not found.", executablePath);
        }

        if (request.ProcessId <= 0 ||
            request.Duration < TimeSpan.FromMilliseconds(250) ||
            request.Duration > TimeSpan.FromSeconds(10) ||
            string.IsNullOrWhiteSpace(request.OutputFilePath) ||
            string.IsNullOrWhiteSpace(request.SessionName))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "PresentMon capture request is outside the supported bounds.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputFilePath))!);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        startInfo.ArgumentList.Add("--process_id");
        startInfo.ArgumentList.Add(request.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--output_file");
        startInfo.ArgumentList.Add(Path.GetFullPath(request.OutputFilePath));
        startInfo.ArgumentList.Add("--timed");
        startInfo.ArgumentList.Add(request.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--terminate_after_timed");
        startInfo.ArgumentList.Add("--session_name");
        startInfo.ArgumentList.Add(request.SessionName);
        startInfo.ArgumentList.Add("--no_console_stats");
        return startInfo;
    }

    private static void TryStopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
            // Cancellation must not be replaced by a best-effort cleanup failure.
        }
    }
}
