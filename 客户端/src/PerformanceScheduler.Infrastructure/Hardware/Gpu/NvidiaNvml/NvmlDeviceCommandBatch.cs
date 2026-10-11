namespace PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;

internal static class NvmlDeviceCommandBatch
{
    internal static bool Execute(uint deviceCount, Func<uint, bool> command, CancellationToken cancellationToken)
    {
        var allSucceeded = deviceCount > 0;
        for (uint index = 0; index < deviceCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var succeeded = command(index);
            allSucceeded &= succeeded;
        }

        return allSucceeded;
    }
}
