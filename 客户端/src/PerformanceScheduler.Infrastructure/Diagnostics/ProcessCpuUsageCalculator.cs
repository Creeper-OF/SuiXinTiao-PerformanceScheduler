namespace PerformanceScheduler.Infrastructure.Diagnostics;

internal static class ProcessCpuUsageCalculator
{
    public static double? Calculate(
        TimeSpan previousProcessorTime,
        TimeSpan currentProcessorTime,
        TimeSpan elapsed,
        int processorCount)
    {
        if (elapsed <= TimeSpan.Zero || processorCount <= 0)
        {
            return null;
        }

        var processorDelta = currentProcessorTime - previousProcessorTime;
        if (processorDelta < TimeSpan.Zero)
        {
            return null;
        }

        var usage = processorDelta.TotalMilliseconds /
                    elapsed.TotalMilliseconds /
                    processorCount * 100d;
        return Math.Round(Math.Clamp(usage, 0d, 100d), 1);
    }
}
