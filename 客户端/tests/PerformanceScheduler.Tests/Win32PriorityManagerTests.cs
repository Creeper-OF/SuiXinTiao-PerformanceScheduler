using System.Diagnostics;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Processes;

namespace PerformanceScheduler.Tests;

public sealed class Win32PriorityManagerTests
{
    [Fact]
    public async Task ProcessIdentity_UsesExecutableImageBeforeProcessInitialization()
    {
        var executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var child = SuspendedTestProcess.Start(executablePath);
        var manager = new Win32PriorityManager();

        var identity = await manager.TryGetProcessIdentityAsync(child.Process.Id);

        Assert.NotNull(identity);
        Assert.Equal(executablePath, identity.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        Assert.True(await manager.IsSameProcessAsync(identity));
    }

    [Fact]
    public void PriorityRead_ReturnsEffectiveValueAfterAnotherHandleChangesIt()
    {
        var executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var child = SuspendedTestProcess.Start(executablePath);
        using var observer = Process.GetProcessById(child.Process.Id);
        using var writer = Process.GetProcessById(child.Process.Id);
        writer.PriorityClass = ProcessPriorityClass.Normal;
        Assert.Equal(ProcessPriorityClass.Normal, observer.PriorityClass);
        writer.PriorityClass = ProcessPriorityClass.BelowNormal;

        Assert.Equal(ProcessPriorityClass.BelowNormal, WindowsProcessStateReader.ReadPriority(observer));
    }

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

    [Fact]
    public async Task ApplyBackgroundPoliciesAsync_FailsWhenEfficiencyStateIsUnknownAndEnableIsRequested()
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
            var processId = process.Id;
            var identity = await manager.TryGetProcessIdentityAsync(processId);
            var priority = await manager.TryGetPriorityAsync(processId);

            Assert.NotNull(identity);
            Assert.NotNull(priority);

            var result = await manager.ApplyBackgroundPoliciesAsync(
                new FocusedAppContext
                {
                    ProcessId = -1,
                    ProcessName = "foreground",
                    WindowTitle = string.Empty,
                    WindowHandle = 0
                },
                new PerformanceProfile(),
                new[]
                {
                    new BackgroundProcessPriorityBaseline
                    {
                        ProcessId = processId,
                        Identity = identity,
                        ProcessName = identity.ProcessName,
                        Category = "Test",
                        PreviousPriority = priority.Value,
                        TargetPriority = priority.Value,
                        PreviousEfficiencyMode = null,
                        TargetEfficiencyMode = true
                    }
                });

            var adjustment = Assert.Single(result);
            Assert.Equal(SchedulerActionStatus.Failed, adjustment.Status);
            Assert.Null(adjustment.AppliedEfficiencyMode);
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
    public async Task ApplyBackgroundPoliciesAsync_VerifiesPriorityAfterWriting()
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
            var previousPriority = await manager.TryGetPriorityAsync(process.Id);

            Assert.NotNull(identity);
            Assert.NotNull(previousPriority);

            var targetPriority = previousPriority == PriorityLevel.BelowNormal
                ? PriorityLevel.Normal
                : PriorityLevel.BelowNormal;
            var result = await manager.ApplyBackgroundPoliciesAsync(
                new FocusedAppContext
                {
                    ProcessId = -1,
                    ProcessName = "foreground",
                    WindowTitle = string.Empty,
                    WindowHandle = 0
                },
                new PerformanceProfile(),
                new[]
                {
                    new BackgroundProcessPriorityBaseline
                    {
                        ProcessId = process.Id,
                        Identity = identity,
                        ProcessName = identity.ProcessName,
                        Category = "Test",
                        PreviousPriority = previousPriority.Value,
                        TargetPriority = targetPriority,
                        PreviousEfficiencyMode = null,
                        TargetEfficiencyMode = false
                    }
                });

            var adjustment = Assert.Single(result);
            if (adjustment.Status != SchedulerActionStatus.Applied)
            {
                var currentIdentity = await manager.TryGetProcessIdentityAsync(process.Id);
                var currentPriority = await manager.TryGetPriorityAsync(process.Id);
                Assert.Fail(
                    $"Background policy returned {adjustment.Status}: {adjustment.Message}. " +
                    $"Priority: original={previousPriority}, target={targetPriority}, " +
                    $"reported={adjustment.AppliedPriority}, observed={currentPriority}. " +
                    $"Identity: captured={identity}, observed={currentIdentity}.");
            }

            Assert.Equal(targetPriority, adjustment.AppliedPriority);
            Assert.Equal(targetPriority, await manager.TryGetPriorityAsync(process.Id));
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
    public async Task ApplyBackgroundPoliciesAsync_DoesNotChangeSchedulerEfficiencyMode()
    {
        var manager = new Win32PriorityManager();
        var processId = Environment.ProcessId;
        var identity = await manager.TryGetProcessIdentityAsync(processId);
        var currentState = await manager.TryGetEfficiencyModeAsync(processId);

        if (identity is null || currentState is null)
        {
            return;
        }

        var result = await manager.ApplyBackgroundPoliciesAsync(
            new FocusedAppContext
            {
                ProcessId = -1,
                ProcessName = "foreground",
                WindowTitle = string.Empty,
                WindowHandle = 0
            },
            new PerformanceProfile(),
            new[]
            {
                new BackgroundProcessPriorityBaseline
                {
                    ProcessId = processId,
                    Identity = identity,
                    ProcessName = identity.ProcessName,
                    Category = "Test",
                    PreviousPriority = PriorityLevel.Normal,
                    TargetPriority = PriorityLevel.Normal,
                    PreviousEfficiencyMode = currentState,
                    TargetEfficiencyMode = !currentState.IsEnabled
                }
            });

        var adjustment = Assert.Single(result);
        Assert.Equal(SchedulerActionStatus.Unsupported, adjustment.Status);
        Assert.Equal(currentState.IsEnabled, (await manager.TryGetEfficiencyModeAsync(processId))?.IsEnabled);
    }
}
