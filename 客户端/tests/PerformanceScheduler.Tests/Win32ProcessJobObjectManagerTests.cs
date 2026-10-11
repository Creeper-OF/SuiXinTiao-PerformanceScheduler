using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Processes;
using Xunit;

namespace PerformanceScheduler.Tests;

public sealed class Win32ProcessJobObjectManagerTests
{
    private static string GetTestExecutablePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    [Fact]
    public async Task ProcessIdentity_ResolvesCorrectly()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        using var manager = new Win32ProcessJobObjectManager();

        var identity = await manager.TryGetProcessIdentityAsync(child.Process.Id);

        Assert.NotNull(identity);
        Assert.Equal(child.Process.Id, identity.ProcessId);
        Assert.Equal(executablePath, identity.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        Assert.True(await manager.IsSameProcessAsync(identity));
    }

    [Fact]
    public async Task ProcessIdentity_ReturnsNullForInvalidPid()
    {
        using var manager = new Win32ProcessJobObjectManager();
        var identity = await manager.TryGetProcessIdentityAsync(-1);
        Assert.Null(identity);
    }

    [Fact]
    public async Task SafetyPolicy_BlocksSelfProcess()
    {
        using var manager = new Win32ProcessJobObjectManager();
        var selfPid = Environment.ProcessId;

        var limits = new JobObjectLimitPreference
        {
            CpuRate = new JobObjectCpuRatePreference
            {
                Enabled = true,
                MaxCpuRatePercent = 25.0,
                HardCap = true
            }
        };

        var applied = await manager.ApplyProcessLimitsAsync(selfPid, limits);
        Assert.False(applied);
    }

    [Fact]
    public async Task ApplyProcessLimits_ConfiguresCpuRateAndMemoryLimit()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        using var manager = new Win32ProcessJobObjectManager();

        var limits = new JobObjectLimitPreference
        {
            CpuRate = new JobObjectCpuRatePreference
            {
                Enabled = true,
                MaxCpuRatePercent = 20.0,
                HardCap = true
            },
            MemoryLimit = new JobObjectMemoryLimitPreference
            {
                Enabled = true,
                MaxCommitMemoryBytes = 256UL * 1024 * 1024
            }
        };

        var applied = await manager.ApplyProcessLimitsAsync(child.Process.Id, limits);
        Assert.True(applied);

        var snapshot = await manager.GetCurrentLimitsAsync(child.Process.Id);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.HasCpuRateControl);
        Assert.Equal(20.0, snapshot.CpuRatePercent);
        Assert.True(snapshot.CpuRateHardCap);
        Assert.True(snapshot.HasMemoryLimit);
        Assert.Equal(256UL * 1024 * 1024, snapshot.MaxCommitMemoryBytes);
    }

    [Fact]
    public async Task RemoveProcessLimits_ClearsConfiguredLimits()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        using var manager = new Win32ProcessJobObjectManager();

        var limits = new JobObjectLimitPreference
        {
            CpuRate = new JobObjectCpuRatePreference
            {
                Enabled = true,
                MaxCpuRatePercent = 30.0,
                HardCap = true
            }
        };

        await manager.ApplyProcessLimitsAsync(child.Process.Id, limits);
        var appliedSnapshot = await manager.GetCurrentLimitsAsync(child.Process.Id);
        Assert.NotNull(appliedSnapshot);
        Assert.True(appliedSnapshot.HasCpuRateControl);

        var removed = await manager.RemoveProcessLimitsAsync(child.Process.Id);
        Assert.True(removed);

        var clearedSnapshot = await manager.GetCurrentLimitsAsync(child.Process.Id);
        Assert.NotNull(clearedSnapshot);
        Assert.False(clearedSnapshot.HasCpuRateControl);
    }

    [Fact]
    public async Task RestoreProcessLimitsIfUnchanged_SucceedsWithValidIdentity()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        IProcessJobObjectManager manager = new Win32ProcessJobObjectManager();

        var identity = await manager.TryGetProcessIdentityAsync(child.Process.Id);
        Assert.NotNull(identity);

        var limits = new JobObjectLimitPreference
        {
            CpuRate = new JobObjectCpuRatePreference
            {
                Enabled = true,
                MaxCpuRatePercent = 15.0,
                HardCap = true
            }
        };

        await manager.ApplyProcessLimitsAsync(child.Process.Id, limits);

        var restoreResult = await manager.RestoreProcessLimitsIfUnchangedAsync(identity);
        Assert.Equal(ConditionalProcessUpdateResult.Applied, restoreResult);

        var currentLimits = await manager.GetCurrentLimitsAsync(child.Process.Id);
        Assert.NotNull(currentLimits);
        Assert.False(currentLimits.HasCpuRateControl);
    }

    [Fact]
    public async Task RestoreProcessLimitsIfUnchanged_DetectsIdentityMismatch()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        IProcessJobObjectManager manager = new Win32ProcessJobObjectManager();

        var identity = await manager.TryGetProcessIdentityAsync(child.Process.Id);
        Assert.NotNull(identity);

        var alteredIdentity = identity with
        {
            StartedAt = identity.StartedAt.AddSeconds(5)
        };

        var restoreResult = await manager.RestoreProcessLimitsIfUnchangedAsync(alteredIdentity);
        Assert.Equal(ConditionalProcessUpdateResult.IdentityMismatch, restoreResult);
    }
}
