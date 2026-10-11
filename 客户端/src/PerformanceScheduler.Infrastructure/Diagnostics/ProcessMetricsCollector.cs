using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Diagnostics;

public sealed class ProcessMetricsCollector : IMetricsCollector
{
    private readonly object _sampleSync = new();
    private readonly Dictionary<int, ProcessSample> _samples = new();

    public Task<MetricsSnapshot> CollectAsync(FocusedAppContext app, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var process = Process.GetProcessById(app.ProcessId);
            var collectedAt = DateTimeOffset.UtcNow;
            var startedAt = process.StartTime.ToUniversalTime();
            var processorTime = process.TotalProcessorTime;
            double? cpuUsagePercent = null;

            lock (_sampleSync)
            {
                if (_samples.TryGetValue(process.Id, out var previous) &&
                    previous.StartedAt == startedAt)
                {
                    cpuUsagePercent = ProcessCpuUsageCalculator.Calculate(
                        previous.ProcessorTime,
                        processorTime,
                        collectedAt - previous.CollectedAt,
                        Environment.ProcessorCount);
                }

                _samples[process.Id] = new ProcessSample(startedAt, collectedAt, processorTime);
                PruneSamples(collectedAt);
            }

            return Task.FromResult(new MetricsSnapshot
            {
                CollectedAt = collectedAt,
                WorkingSetMb = Math.Round(process.WorkingSet64 / 1024d / 1024d, 2),
                PrivateMemoryMb = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 2),
                ThreadCount = process.Threads.Count,
                HandleCount = process.HandleCount,
                CpuUsagePercent = cpuUsagePercent,
                TotalProcessorTime = processorTime
            });
        }
        catch
        {
            return Task.FromResult(new MetricsSnapshot
            {
                CollectedAt = DateTimeOffset.UtcNow,
                IsAvailable = false
            });
        }
    }

    private void PruneSamples(DateTimeOffset now)
    {
        if (_samples.Count <= 128)
        {
            return;
        }

        var staleProcessIds = _samples
            .Where(pair => now - pair.Value.CollectedAt > TimeSpan.FromMinutes(10))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var processId in staleProcessIds)
        {
            _samples.Remove(processId);
        }

        foreach (var processId in _samples
                     .OrderBy(pair => pair.Value.CollectedAt)
                     .Take(Math.Max(0, _samples.Count - 128))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _samples.Remove(processId);
        }
    }

    private sealed record ProcessSample(
        DateTime StartedAt,
        DateTimeOffset CollectedAt,
        TimeSpan ProcessorTime);
}
