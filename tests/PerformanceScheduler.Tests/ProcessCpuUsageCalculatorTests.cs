using PerformanceScheduler.Infrastructure.Diagnostics;

namespace PerformanceScheduler.Tests;

public sealed class ProcessCpuUsageCalculatorTests
{
    [Fact]
    public void Calculate_NormalizesUsageAcrossLogicalProcessors()
    {
        var usage = ProcessCpuUsageCalculator.Calculate(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(2),
            processorCount: 4);

        Assert.Equal(25d, usage);
    }

    [Fact]
    public void Calculate_RejectsInvalidOrReusedProcessSamples()
    {
        Assert.Null(ProcessCpuUsageCalculator.Calculate(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            processorCount: 4));
        Assert.Null(ProcessCpuUsageCalculator.Calculate(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            processorCount: 4));
    }
}
