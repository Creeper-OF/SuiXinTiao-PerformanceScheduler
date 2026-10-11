using PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;

namespace PerformanceScheduler.Tests;

public sealed class NvmlDeviceCommandBatchTests
{
    [Fact]
    public void Execute_WhenOneDeviceFails_StillAttemptsRemainingDevicesAndReportsFailure()
    {
        var attempted = new List<uint>();

        var success = NvmlDeviceCommandBatch.Execute(3, index =>
        {
            attempted.Add(index);
            return index != 1;
        }, CancellationToken.None);

        Assert.False(success);
        Assert.Equal(new uint[] { 0, 1, 2 }, attempted);
    }

    [Fact]
    public void Execute_WhenEveryDeviceSucceeds_ReportsSuccess()
    {
        var restored = new bool[2];

        var success = NvmlDeviceCommandBatch.Execute(2, index => restored[index] = true, CancellationToken.None);

        Assert.True(success);
        Assert.All(restored, Assert.True);
    }

    [Fact]
    public void Execute_WithoutDevices_DoesNotReportSuccessfulRecovery()
    {
        Assert.False(NvmlDeviceCommandBatch.Execute(0, _ => throw new InvalidOperationException(), CancellationToken.None));
    }

    [Fact]
    public void Execute_WhenCancelledAfterFirstDevice_StopsRemainingCommands()
    {
        using var cancellation = new CancellationTokenSource();
        var attempted = new List<uint>();

        Assert.ThrowsAny<OperationCanceledException>(() => NvmlDeviceCommandBatch.Execute(2, index =>
        {
            attempted.Add(index);
            cancellation.Cancel();
            return true;
        }, cancellation.Token));

        Assert.Equal(new uint[] { 0 }, attempted);
    }
}
