using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Diagnostics;

public sealed class ProcessMetricsCollector : IMetricsCollector
{
    public Task<MetricsSnapshot> CollectAsync(FocusedAppContext app, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(app.ProcessId);
            return Task.FromResult(new MetricsSnapshot
            {
                CollectedAt = DateTimeOffset.Now,
                WorkingSetMb = Math.Round(process.WorkingSet64 / 1024d / 1024d, 2),
                ThreadCount = process.Threads.Count,
                TotalProcessorTime = process.TotalProcessorTime
            });
        }
        catch
        {
            return Task.FromResult(new MetricsSnapshot
            {
                CollectedAt = DateTimeOffset.Now
            });
        }
    }
}
