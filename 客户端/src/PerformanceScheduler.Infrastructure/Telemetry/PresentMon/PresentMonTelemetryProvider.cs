using System.Diagnostics;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;

namespace PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

public sealed class PresentMonTelemetryProvider : ITelemetryProvider
{
    public const string Id = "windows.presentmon";

    private readonly IPresentMonExecutableLocator _executableLocator;
    private readonly IPresentMonCollectorConflictDetector _conflictDetector;
    private readonly IPresentMonCaptureRunner _captureRunner;
    private readonly PresentMonCsvParser _csvParser;
    private readonly string _workingDirectory;
    private readonly TimeSpan _captureDuration;

    public PresentMonTelemetryProvider(
        IPresentMonExecutableLocator executableLocator,
        IPresentMonCollectorConflictDetector conflictDetector,
        IPresentMonCaptureRunner captureRunner,
        PresentMonCsvParser csvParser,
        string workingDirectory,
        TimeSpan? captureDuration = null)
    {
        _executableLocator = executableLocator;
        _conflictDetector = conflictDetector;
        _captureRunner = captureRunner;
        _csvParser = csvParser;
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _captureDuration = captureDuration ?? TimeSpan.FromSeconds(1);
        if (_captureDuration < TimeSpan.FromMilliseconds(250) || _captureDuration > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(captureDuration));
        }
    }

    public string ProviderId => Id;

    public Task<TelemetryProviderCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isSupported = _executableLocator.FindExecutable() is not null;
        return Task.FromResult(new TelemetryProviderCapabilities
        {
            ProviderId = ProviderId,
            IsSupported = isSupported,
            SupportedMetrics =
            [
                TelemetryMetricKind.FramesPerSecondAverage,
                TelemetryMetricKind.FrameTimeAverageMilliseconds,
                TelemetryMetricKind.FrameTimeP95Milliseconds,
                TelemetryMetricKind.FrameTimeP99Milliseconds,
                TelemetryMetricKind.PresentedFrameCount
            ],
            MinimumSamplingInterval = TimeSpan.FromSeconds(5),
            EstimatedOverhead = TelemetryOverheadClass.Medium,
            UnavailableReasonCode = isSupported ? string.Empty : TelemetryReasonCodes.PresentMonUnavailable
        });
    }

    public async Task<TelemetryProviderSample> CollectAsync(
        FocusedAppContext target,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var executablePath = _executableLocator.FindExecutable();
        if (executablePath is null)
        {
            return Status(
                TelemetryCollectionStatus.Unavailable,
                TelemetryReasonCodes.PresentMonUnavailable,
                collectionDuration: Stopwatch.GetElapsedTime(startedAt));
        }

        var conflicts = _conflictDetector.FindPotentialConflicts();
        if (conflicts.Count > 0)
        {
            return Status(
                TelemetryCollectionStatus.Unavailable,
                TelemetryReasonCodes.PresentMonCollectorConflict,
                string.Join(", ", conflicts),
                Stopwatch.GetElapsedTime(startedAt));
        }

        var captureDirectory = Path.Combine(_workingDirectory, Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(captureDirectory, "frames.csv");
        try
        {
            Directory.CreateDirectory(captureDirectory);
            var result = await _captureRunner.CaptureAsync(
                executablePath,
                new PresentMonCaptureRequest
                {
                    ProcessId = target.ProcessId,
                    Duration = _captureDuration,
                    OutputFilePath = outputPath,
                    SessionName = $"SuiXinTiao-{Environment.ProcessId}-{Guid.NewGuid():N}"
                },
                cancellationToken);
            if (result.ExitCode != 0)
            {
                var reasonCode = IsAccessDenied(result.StandardError)
                    ? TelemetryReasonCodes.PresentMonAccessDenied
                    : TelemetryReasonCodes.PresentMonCaptureFailed;
                return Status(
                    TelemetryCollectionStatus.Failed,
                    reasonCode,
                    Truncate(result.StandardError),
                    Stopwatch.GetElapsedTime(startedAt));
            }

            if (!File.Exists(outputPath))
            {
                return Status(
                    TelemetryCollectionStatus.Unavailable,
                    TelemetryReasonCodes.PresentMonNoFrameSamples,
                    collectionDuration: Stopwatch.GetElapsedTime(startedAt));
            }

            PresentMonFrameStatistics? statistics;
            try
            {
                statistics = await _csvParser.ParseAsync(outputPath, target.ProcessId, cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                return Status(
                    TelemetryCollectionStatus.Failed,
                    TelemetryReasonCodes.PresentMonInvalidOutput,
                    Truncate(exception.Message),
                    Stopwatch.GetElapsedTime(startedAt));
            }

            if (statistics is null)
            {
                return Status(
                    TelemetryCollectionStatus.Unavailable,
                    TelemetryReasonCodes.PresentMonNoFrameSamples,
                    collectionDuration: Stopwatch.GetElapsedTime(startedAt));
            }

            return new TelemetryProviderSample
            {
                ProviderId = ProviderId,
                Status = TelemetryCollectionStatus.Collected,
                CollectionDuration = Stopwatch.GetElapsedTime(startedAt),
                Metrics =
                [
                    Metric(TelemetryMetricKind.FramesPerSecondAverage, statistics.AverageFramesPerSecond, "FPS"),
                    Metric(TelemetryMetricKind.FrameTimeAverageMilliseconds, statistics.AverageFrameTimeMilliseconds, "ms"),
                    Metric(TelemetryMetricKind.FrameTimeP95Milliseconds, statistics.FrameTimeP95Milliseconds, "ms"),
                    Metric(TelemetryMetricKind.FrameTimeP99Milliseconds, statistics.FrameTimeP99Milliseconds, "ms"),
                    Metric(TelemetryMetricKind.PresentedFrameCount, statistics.SampleCount, "count")
                ]
            };
        }
        finally
        {
            TryDeleteDirectory(captureDirectory);
        }
    }

    private static TelemetryProviderSample Status(
        TelemetryCollectionStatus status,
        string reasonCode,
        string technicalMessage = "",
        TimeSpan collectionDuration = default) =>
        new()
        {
            ProviderId = Id,
            Status = status,
            ReasonCode = reasonCode,
            TechnicalMessage = technicalMessage,
            CollectionDuration = collectionDuration
        };

    private static TelemetryMetricValue Metric(TelemetryMetricKind kind, double value, string unit) =>
        new() { Kind = kind, Value = value, Unit = unit };

    private static string Truncate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, 1000)];
    }

    private static bool IsAccessDenied(string message) =>
        message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary capture cleanup is best effort and never changes the observation result.
        }
    }
}
