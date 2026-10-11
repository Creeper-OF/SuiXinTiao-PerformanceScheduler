using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Telemetry.Abstractions;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;

namespace PerformanceScheduler.Tests;

public sealed class ResourceArbitrationProviderTests
{
    private static readonly DateTimeOffset SampleStartTime = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetArbitrationSummaryAsync_WhenOptInIsFalse_ReturnsNullWithoutCollectingCandidates()
    {
        var candidateProvider = new RecordingCandidateProvider();
        var provider = new ResourceArbitrationProvider(candidateProvider);

        var foregroundApp = CreateAppContext(100, "game.exe");
        var profile = new PerformanceProfile
        {
            Name = "Default Profile",
            Priority = new PriorityPreference { UseResourceArbitration = false }
        };

        var result = await provider.GetArbitrationSummaryAsync(foregroundApp, profile);

        Assert.Null(result);
        Assert.Equal(0, candidateProvider.CollectCallCount);
    }

    [Fact]
    public async Task GetArbitrationSummaryAsync_WhenCandidateProviderThrows_ReturnsNullGracefully()
    {
        var candidateProvider = new ThrowingCandidateProvider();
        var logger = new RecordingLogger();
        var provider = new ResourceArbitrationProvider(candidateProvider, logger: logger);

        var foregroundApp = CreateAppContext(100, "game.exe");
        var profile = new PerformanceProfile
        {
            Name = "Arbitration Profile",
            Priority = new PriorityPreference { UseResourceArbitration = true }
        };

        var result = await provider.GetArbitrationSummaryAsync(foregroundApp, profile);

        Assert.Null(result);
        Assert.Contains(logger.Messages, msg => msg.Contains("Resource arbitration candidate collection failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetArbitrationSummaryAsync_WhenContentionProviderReturnsHighContention_RecommendsMeasuredCandidates()
    {
        var candidate = new ResourceArbitrationCandidate
        {
            ProcessId = 200,
            ProcessName = "heavy-worker.exe",
            StartedAt = SampleStartTime,
            Classification = ProcessClassification.Productivity,
            HasApplicablePolicy = true,
            CpuUsagePercent = 45.0
        };
        var candidateProvider = new RecordingCandidateProvider([candidate]);
        var contention = new ResourceContentionSummary
        {
            Level = ResourceContentionLevel.High,
            ForegroundCpuUsagePercent = 20.0,
            SystemCpuUsagePercent = 95.0
        };
        var provider = new ResourceArbitrationProvider(
            candidateProvider,
            contentionProvider: (_, _) => Task.FromResult<ResourceContentionSummary?>(contention));

        var foregroundApp = CreateAppContext(100, "game.exe");
        var profile = new PerformanceProfile
        {
            Name = "Gaming Profile",
            Priority = new PriorityPreference { UseResourceArbitration = true }
        };

        var result = await provider.GetArbitrationSummaryAsync(foregroundApp, profile);

        Assert.NotNull(result);
        Assert.Equal(ResourceArbitrationState.RecommendBackgroundRelief, result.State);
        Assert.Equal(ResourceContentionLevel.High, result.ContentionLevel);
        Assert.Single(result.Recommendations);
        Assert.Equal(200, result.Recommendations[0].ProcessId);
        Assert.Equal(SampleStartTime, result.Recommendations[0].StartedAt);
    }

    [Fact]
    public async Task GetArbitrationSummaryAsync_WhenContentionIsNone_ProducesNoContentionState()
    {
        var candidate = new ResourceArbitrationCandidate
        {
            ProcessId = 200,
            ProcessName = "idle-worker.exe",
            StartedAt = SampleStartTime,
            Classification = ProcessClassification.Productivity,
            HasApplicablePolicy = true,
            CpuUsagePercent = 5.0
        };
        var candidateProvider = new RecordingCandidateProvider([candidate]);
        var contention = new ResourceContentionSummary
        {
            Level = ResourceContentionLevel.None,
            ForegroundCpuUsagePercent = 50.0,
            SystemCpuUsagePercent = 55.0
        };
        var provider = new ResourceArbitrationProvider(
            candidateProvider,
            contentionProvider: (_, _) => Task.FromResult<ResourceContentionSummary?>(contention));

        var foregroundApp = CreateAppContext(100, "game.exe");
        var profile = new PerformanceProfile
        {
            Name = "Gaming Profile",
            Priority = new PriorityPreference { UseResourceArbitration = true }
        };

        var result = await provider.GetArbitrationSummaryAsync(foregroundApp, profile);

        Assert.NotNull(result);
        Assert.Equal(ResourceArbitrationState.NoContention, result.State);
        Assert.Empty(result.Recommendations);
    }

    [Fact]
    public async Task GetArbitrationSummaryAsync_WhenNoContentionOrHistoryExists_ProducesInsufficientEvidenceState()
    {
        var candidate = new ResourceArbitrationCandidate
        {
            ProcessId = 200,
            ProcessName = "worker.exe",
            StartedAt = SampleStartTime,
            Classification = ProcessClassification.Productivity,
            HasApplicablePolicy = true,
            CpuUsagePercent = 50.0
        };
        var candidateProvider = new RecordingCandidateProvider([candidate]);
        var historyStore = new RecordingHistoryStore();
        var provider = new ResourceArbitrationProvider(
            candidateProvider,
            historyStore: historyStore);

        var foregroundApp = CreateAppContext(100, "game.exe");
        var profile = new PerformanceProfile
        {
            Name = "Gaming Profile",
            Priority = new PriorityPreference { UseResourceArbitration = true }
        };

        var result = await provider.GetArbitrationSummaryAsync(foregroundApp, profile);

        Assert.NotNull(result);
        Assert.Equal(ResourceArbitrationState.InsufficientEvidence, result.State);
        Assert.Empty(result.Recommendations);
    }

    [Fact]
    public async Task GetArbitrationSummaryAsync_ResolvesContentionFromTelemetryHistoryStore()
    {
        var candidate = new ResourceArbitrationCandidate
        {
            ProcessId = 200,
            ProcessName = "heavy-worker.exe",
            StartedAt = SampleStartTime,
            Classification = ProcessClassification.Productivity,
            HasApplicablePolicy = true,
            CpuUsagePercent = 60.0
        };
        var candidateProvider = new RecordingCandidateProvider([candidate]);
        var foregroundApp = CreateAppContext(100, "game.exe");
        var historyStore = new RecordingHistoryStore(
        [
            new TelemetryObservationHistoryEntry
            {
                StorageId = 1,
                Observation = new TelemetryObservation
                {
                    StartedAt = SampleStartTime,
                    TotalDuration = TimeSpan.FromMilliseconds(50),
                    Target = foregroundApp
                },
                ResourceContention = new ResourceContentionSummary
                {
                    Level = ResourceContentionLevel.High,
                    ForegroundCpuUsagePercent = 25.0,
                    SystemCpuUsagePercent = 90.0
                }
            }
        ]);
        var provider = new ResourceArbitrationProvider(
            candidateProvider,
            historyStore: historyStore);

        var profile = new PerformanceProfile
        {
            Name = "Gaming Profile",
            Priority = new PriorityPreference { UseResourceArbitration = true }
        };

        var result = await provider.GetArbitrationSummaryAsync(foregroundApp, profile);

        Assert.NotNull(result);
        Assert.Equal(ResourceArbitrationState.RecommendBackgroundRelief, result.State);
        Assert.Single(result.Recommendations);
        Assert.Equal(200, result.Recommendations[0].ProcessId);
    }

    private static FocusedAppContext CreateAppContext(int processId, string processName) => new()
    {
        ProcessId = processId,
        ProcessName = processName,
        WindowHandle = (nint)1234,
        WindowTitle = processName,
        ExecutablePath = $"C:\\Games\\{processName}"
    };

    private sealed class RecordingCandidateProvider : IResourceArbitrationCandidateProvider
    {
        private readonly IReadOnlyList<ResourceArbitrationCandidate> _candidates;

        public RecordingCandidateProvider(IReadOnlyList<ResourceArbitrationCandidate>? candidates = null)
        {
            _candidates = candidates ?? [];
        }

        public int CollectCallCount { get; private set; }

        public Task<IReadOnlyList<ResourceArbitrationCandidate>> CollectAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile? profile = null,
            CancellationToken cancellationToken = default)
        {
            CollectCallCount++;
            return Task.FromResult(_candidates);
        }
    }

    private sealed class ThrowingCandidateProvider : IResourceArbitrationCandidateProvider
    {
        public Task<IReadOnlyList<ResourceArbitrationCandidate>> CollectAsync(
            FocusedAppContext foregroundApp,
            PerformanceProfile? profile = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Failed to enumerate processes.");
        }
    }

    private sealed class RecordingHistoryStore : ITelemetryHistoryStore
    {
        private readonly IReadOnlyList<TelemetryObservationHistoryEntry> _entries;

        public RecordingHistoryStore(IReadOnlyList<TelemetryObservationHistoryEntry>? entries = null)
        {
            _entries = entries ?? [];
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordObservationAsync(TelemetryObservation observation, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<TelemetryObservationHistoryEntry>> LoadRecentAsync(int count = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries);

        public Task<int> PruneAsync(TelemetryHistoryRetentionPolicy policy, DateTimeOffset? now = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];

        public event EventHandler<LogEntry>? EntryWritten;

        public IReadOnlyList<LogEntry> GetRecentEntries(int count = 200) => [];

        public void Trace(string message) => Write(message);
        public void Debug(string message) => Write(message);
        public void Info(string message) => Write(message);
        public void Warn(string message) => Write(message);
        public void Error(string message, Exception? exception = null) => Write(message);
        public void PublishUserEvent(UserEventCode eventCode, AppLogLevel level, string message, params object?[] parameters) => Write(message);

        private void Write(string message)
        {
            Messages.Add(message);
            EntryWritten?.Invoke(this, new LogEntry(DateTimeOffset.UtcNow, AppLogLevel.Information, message));
        }
    }
}
