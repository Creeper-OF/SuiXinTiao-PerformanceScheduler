using System.IO.Compression;
using System.Text;
using PerformanceScheduler.App.Services.Diagnostics;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Diagnostics;

namespace PerformanceScheduler.Tests;

public sealed class DiagnosticPackageServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "PerformanceScheduler.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Sanitizer_RemovesPrivateRunAndLogValues()
    {
        var sanitizer = new DiagnosticDataSanitizer();
        const string profileName = "My Private Game Profile";
        const string windowTitle = "Confidential Document - Alice";
        const string privatePath = @"C:\Users\Alice\Secret\profile.json";
        var run = CreateRun(profileName, windowTitle, privatePath);
        var log = new LogEntry(
            DateTimeOffset.UtcNow,
            AppLogLevel.Error,
            $"Profile {profileName} failed to read {privatePath}.");

        var sanitizedRun = Assert.Single(sanitizer.SanitizeRuns([run]));
        var sanitizedLog = Assert.Single(sanitizer.SanitizeLogs([log], [run]));
        var combined = string.Join(
            "|",
            sanitizedRun.ProfileId,
            sanitizedRun.ProfileName,
            sanitizedRun.WindowTitle,
            sanitizedRun.Summary,
            Assert.Single(sanitizedRun.Actions).Target,
            sanitizedLog.Message);

        Assert.DoesNotContain(profileName, combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(windowTitle, combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privatePath, combined, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<profile:", sanitizedRun.ProfileId, StringComparison.Ordinal);
        Assert.Equal("<redacted>", sanitizedRun.WindowTitle);
        Assert.Contains("<path>", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_CreatesExpectedRedactedArchive()
    {
        Directory.CreateDirectory(_tempDirectory);
        const string profileName = "Private Strategy";
        const string windowTitle = "Private Window Title";
        const string privatePath = @"D:\Private\settings.json";
        var logger = new FileAppLogger(Path.Combine(_tempDirectory, "logs"));
        logger.Error($"Profile {profileName} failed at {privatePath}.");
        var settingsStore = new JsonAppSettingsStore(Path.Combine(_tempDirectory, "settings.json"));
        var service = new DiagnosticPackageService(
            new FakeRuntimeStateStore([CreateRun(profileName, windowTitle, privatePath)]),
            new FakeDeviceFingerprintProvider(),
            new FakeCapabilityDetector(),
            logger,
            settingsStore);
        var destinationPath = Path.Combine(_tempDirectory, "diagnostics.sxtdiag.zip");

        await service.ExportAsync(destinationPath);

        using var archive = ZipFile.OpenRead(destinationPath);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "README.txt",
                "manifest.json",
                "device.json",
                "capabilities.json",
                "runs.json",
                "logs.json",
                "safety-settings.json",
                "collection-errors.json"
            },
            entryNames);

        var archiveText = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain(profileName, archiveText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(windowTitle, archiveText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privatePath, archiveText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("随芯调诊断包", archiveText, StringComparison.Ordinal);
        Assert.Contains("default-redacted", archiveText, StringComparison.Ordinal);
        Assert.Contains(@"\u003Cprofile:", archiveText, StringComparison.Ordinal);
        Assert.Contains(@"\u003Cpath\u003E", archiveText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_WhenACollectorFails_StillCreatesPackageWithErrorRecord()
    {
        Directory.CreateDirectory(_tempDirectory);
        var logger = new FileAppLogger(Path.Combine(_tempDirectory, "logs"));
        var service = new DiagnosticPackageService(
            new ThrowingRuntimeStateStore(),
            new FakeDeviceFingerprintProvider(),
            new FakeCapabilityDetector(),
            logger,
            new JsonAppSettingsStore(Path.Combine(_tempDirectory, "settings.json")));
        var destinationPath = Path.Combine(_tempDirectory, "partial.sxtdiag.zip");

        await service.ExportAsync(destinationPath);

        using var archive = ZipFile.OpenRead(destinationPath);
        var errors = ReadEntry(Assert.Single(
            archive.Entries,
            entry => entry.FullName == "collection-errors.json"));
        Assert.Contains("runtime-history", errors, StringComparison.Ordinal);
        Assert.Contains(nameof(IOException), errors, StringComparison.Ordinal);
        Assert.NotNull(archive.GetEntry("device.json"));
        Assert.NotNull(archive.GetEntry("logs.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static SchedulerRunHistoryEntry CreateRun(
        string profileName,
        string windowTitle,
        string privatePath) =>
        new()
        {
            RunId = Guid.NewGuid(),
            TransactionId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            ProcessName = "example.exe",
            WindowTitle = windowTitle,
            ProfileId = "private-profile-id",
            ProfileName = profileName,
            Summary = $"Applied {profileName} to {windowTitle} from {privatePath}.",
            Actions =
            [
                new SchedulerActionResult
                {
                    Plan = new SchedulerActionPlan
                    {
                        Kind = SchedulerActionKind.ForegroundPriority,
                        Target = privatePath,
                        OriginalValue = "Normal",
                        TargetValue = "High",
                        RequiresRollback = true
                    },
                    Status = SchedulerActionStatus.Failed,
                    ReasonCode = SchedulerActionReasonCode.ProviderApplyFailed,
                    Message = $"Provider rejected {privatePath}."
                }
            ]
        };

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class FakeRuntimeStateStore(IReadOnlyList<SchedulerRunHistoryEntry> runs) : IRuntimeStateStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordRunAsync(SchedulerRunResult result, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SchedulerRunHistoryEntry>> LoadRecentRunsAsync(
            int count = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchedulerRunHistoryEntry>>(runs.Take(count).ToArray());
    }

    private sealed class ThrowingRuntimeStateStore : IRuntimeStateStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordRunAsync(SchedulerRunResult result, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SchedulerRunHistoryEntry>> LoadRecentRunsAsync(
            int count = 50,
            CancellationToken cancellationToken = default) =>
            throw new IOException(@"Database at C:\Users\Alice\runtime\scheduler.db is unavailable.");
    }

    private sealed class FakeDeviceFingerprintProvider : IDeviceFingerprintProvider
    {
        public Task<DeviceFingerprint> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceFingerprint
            {
                Manufacturer = "Test Vendor",
                Model = "Test Model",
                CpuName = "Test CPU",
                GpuNames = ["Test GPU"],
                TotalMemoryBytes = 16UL * 1024 * 1024 * 1024
            });
    }

    private sealed class FakeCapabilityDetector : ICapabilityDetector
    {
        public Task<CapabilitySnapshot> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapabilitySnapshot
            {
                SupportsPowerPlanSwitching = true,
                SupportsPriorityBoost = true,
                SupportsRollback = true,
                SupportsMetricsCollection = true
            });
    }
}
