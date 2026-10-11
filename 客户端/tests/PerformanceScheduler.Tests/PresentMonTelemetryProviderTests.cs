using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

namespace PerformanceScheduler.Tests;

public sealed class PresentMonTelemetryProviderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetCapabilitiesAsync_WithoutExecutable_IsUnsupported()
    {
        var provider = CreateProvider(new FakeLocator(null), new FakeConflictDetector(), new FakeRunner());

        var capabilities = await provider.GetCapabilitiesAsync();

        Assert.False(capabilities.IsSupported);
        Assert.Equal(TelemetryReasonCodes.PresentMonUnavailable, capabilities.UnavailableReasonCode);
    }

    [Fact]
    public async Task CollectAsync_WithPotentialConflict_DoesNotStartCapture()
    {
        var runner = new FakeRunner();
        var provider = CreateProvider(
            new FakeLocator("PresentMon.exe"),
            new FakeConflictDetector(["CapFrameX"]),
            runner);

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Unavailable, sample.Status);
        Assert.Equal(TelemetryReasonCodes.PresentMonCollectorConflict, sample.ReasonCode);
        Assert.Contains("CapFrameX", sample.TechnicalMessage);
        Assert.Equal(0, runner.CaptureCount);
    }

    [Fact]
    public async Task CollectAsync_WithValidCapture_ReturnsFrameMetricsAndCleansTemporaryFiles()
    {
        var runner = new FakeRunner
        {
            CaptureHandler = async request =>
            {
                await File.WriteAllTextAsync(
                    request.OutputFilePath,
                    "Application,ProcessID,MsBetweenPresents\nGame.exe,42,10\nGame.exe,42,20\n");
                return new PresentMonCaptureResult();
            }
        };
        var provider = CreateProvider(
            new FakeLocator("PresentMon.exe"),
            new FakeConflictDetector(),
            runner);

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Collected, sample.Status);
        Assert.Equal(5, sample.Metrics.Count);
        Assert.Contains(sample.Metrics, metric =>
            metric.Kind == TelemetryMetricKind.FramesPerSecondAverage &&
            Math.Abs(metric.Value - (1000d / 15d)) < 0.001);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_tempDirectory));
    }

    [Fact]
    public async Task CollectAsync_WithInvalidCsv_ReturnsStableFailureReason()
    {
        var runner = new FakeRunner
        {
            CaptureHandler = async request =>
            {
                await File.WriteAllTextAsync(request.OutputFilePath, "Unknown\n1\n");
                return new PresentMonCaptureResult();
            }
        };
        var provider = CreateProvider(
            new FakeLocator("PresentMon.exe"),
            new FakeConflictDetector(),
            runner);

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Failed, sample.Status);
        Assert.Equal(TelemetryReasonCodes.PresentMonInvalidOutput, sample.ReasonCode);
    }

    [Fact]
    public async Task CollectAsync_WhenEtwAccessIsDenied_ReturnsPermissionReason()
    {
        var runner = new FakeRunner
        {
            CaptureHandler = _ => Task.FromResult(new PresentMonCaptureResult
            {
                ExitCode = 5,
                StandardError = "failed to start trace session (access denied)"
            })
        };
        var provider = CreateProvider(
            new FakeLocator("PresentMon.exe"),
            new FakeConflictDetector(),
            runner);

        var sample = await provider.CollectAsync(CreateTarget());

        Assert.Equal(TelemetryCollectionStatus.Failed, sample.Status);
        Assert.Equal(TelemetryReasonCodes.PresentMonAccessDenied, sample.ReasonCode);
    }

    private PresentMonTelemetryProvider CreateProvider(
        IPresentMonExecutableLocator locator,
        IPresentMonCollectorConflictDetector conflictDetector,
        IPresentMonCaptureRunner runner) =>
        new(
            locator,
            conflictDetector,
            runner,
            new PresentMonCsvParser(),
            _tempDirectory,
            TimeSpan.FromSeconds(1));

    private static FocusedAppContext CreateTarget() =>
        new()
        {
            ProcessId = 42,
            ProcessName = "Game",
            WindowTitle = "Game",
            WindowHandle = (nint)1
        };

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class FakeLocator(string? executablePath) : IPresentMonExecutableLocator
    {
        public string? FindExecutable() => executablePath;
    }

    private sealed class FakeConflictDetector(IReadOnlyList<string>? conflicts = null)
        : IPresentMonCollectorConflictDetector
    {
        public IReadOnlyList<string> FindPotentialConflicts() => conflicts ?? [];
    }

    private sealed class FakeRunner : IPresentMonCaptureRunner
    {
        public int CaptureCount { get; private set; }

        public Func<PresentMonCaptureRequest, Task<PresentMonCaptureResult>>? CaptureHandler { get; init; }

        public Task<PresentMonCaptureResult> CaptureAsync(
            string executablePath,
            PresentMonCaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            return CaptureHandler?.Invoke(request) ?? Task.FromResult(new PresentMonCaptureResult());
        }
    }
}
