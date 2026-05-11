using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Processes;

namespace PerformanceScheduler.Tests;

public sealed class Win32PriorityManagerTests
{
    [Fact]
    public async Task ApplyForegroundBoostAsync_BlocksRealTimePriority()
    {
        var manager = new Win32PriorityManager();
        var app = new FocusedAppContext
        {
            ProcessId = Environment.ProcessId,
            ProcessName = Environment.ProcessPath is { } processPath
                ? Path.GetFileNameWithoutExtension(processPath)
                : "PerformanceScheduler.Tests",
            WindowTitle = string.Empty,
            WindowHandle = 0
        };

        var result = await manager.ApplyForegroundBoostAsync(
            app,
            new PerformanceProfile
            {
                Priority = new PriorityPreference
                {
                    ForegroundPriority = PriorityLevel.RealTime
                }
            });

        Assert.Equal(SchedulerActionStatus.Unsupported, result.Status);
        Assert.Contains("RealTime", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
