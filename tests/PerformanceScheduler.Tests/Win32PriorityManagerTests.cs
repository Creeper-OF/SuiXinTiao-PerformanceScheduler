using System.Diagnostics;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Processes;

namespace PerformanceScheduler.Tests;

public sealed class Win32PriorityManagerTests
{
    [Fact]
    public async Task ProcessIdentity_DetectsChangedStartTime()
    {
        var manager = new Win32PriorityManager();
        var identity = await manager.TryGetProcessIdentityAsync(Environment.ProcessId);

        Assert.NotNull(identity);
        Assert.True(await manager.IsSameProcessAsync(identity));
        Assert.False(await manager.IsSameProcessAsync(identity with
        {
            StartedAt = identity.StartedAt.AddSeconds(1)
        }));
    }

    [Fact]
    public async Task ConditionalUpdates_CanReapplyCurrentProcessStateWithoutChangingIt()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 15",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);

        var manager = new Win32PriorityManager();
        try
        {
            var identity = await manager.TryGetProcessIdentityAsync(process.Id);
            var priority = await manager.TryGetPriorityAsync(process.Id);
            var efficiencyMode = await manager.TryGetEfficiencyModeAsync(process.Id);

            Assert.NotNull(identity);
            Assert.NotNull(priority);
            Assert.NotNull(efficiencyMode);
            Assert.Equal(
                ConditionalProcessUpdateResult.Applied,
                await manager.RestorePriorityIfUnchangedAsync(identity, priority.Value, priority.Value));
            Assert.Equal(
                ConditionalProcessUpdateResult.Applied,
                await manager.RestoreEfficiencyModeIfUnchangedAsync(
                    identity,
                    efficiencyMode.IsEnabled,
                    efficiencyMode.IsEnabled));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

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
