using System.Diagnostics;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Processes;
using Xunit;

namespace PerformanceScheduler.Tests;

public sealed class Win32ProcessMemoryIoManagerTests
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
        IProcessMemoryIoManager manager = new Win32ProcessMemoryIoManager();

        var identity = await manager.TryGetProcessIdentityAsync(child.Process.Id);

        Assert.NotNull(identity);
        Assert.Equal(child.Process.Id, identity.ProcessId);
        Assert.Equal(executablePath, identity.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        Assert.True(await manager.IsSameProcessAsync(identity));
    }

    [Fact]
    public async Task ProcessIdentity_ReturnsNullForInvalidPid()
    {
        IProcessMemoryIoManager manager = new Win32ProcessMemoryIoManager();
        var identity = await manager.TryGetProcessIdentityAsync(-1);
        Assert.Null(identity);
    }

    [Fact]
    public async Task IoPriority_SetAndGet_VerifiesReadBack()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        IProcessMemoryIoManager manager = new Win32ProcessMemoryIoManager();

        var initial = await manager.TryGetIoPriorityAsync(child.Process.Id);
        Assert.NotNull(initial);

        // Set to VeryLow
        var setVeryLow = await manager.SetIoPriorityAsync(child.Process.Id, IoPriorityLevel.VeryLow);
        Assert.True(setVeryLow);
        var readVeryLow = await manager.TryGetIoPriorityAsync(child.Process.Id);
        Assert.Equal(IoPriorityLevel.VeryLow, readVeryLow);

        // Set to Low
        var setLow = await manager.SetIoPriorityAsync(child.Process.Id, IoPriorityLevel.Low);
        Assert.True(setLow);
        var readLow = await manager.TryGetIoPriorityAsync(child.Process.Id);
        Assert.Equal(IoPriorityLevel.Low, readLow);

        // Set to Normal
        var setNormal = await manager.SetIoPriorityAsync(child.Process.Id, IoPriorityLevel.Normal);
        Assert.True(setNormal);
        var readNormal = await manager.TryGetIoPriorityAsync(child.Process.Id);
        Assert.Equal(IoPriorityLevel.Normal, readNormal);
    }

    [Fact]
    public async Task MemoryPriority_SetAndGet_VerifiesReadBack()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        IProcessMemoryIoManager manager = new Win32ProcessMemoryIoManager();

        var initial = await manager.TryGetMemoryPriorityAsync(child.Process.Id);
        Assert.NotNull(initial);

        // Set to BelowNormal (4)
        var setBelowNormal = await manager.SetMemoryPriorityAsync(child.Process.Id, MemoryPriorityLevel.BelowNormal);
        Assert.True(setBelowNormal);
        var readBelowNormal = await manager.TryGetMemoryPriorityAsync(child.Process.Id);
        Assert.Equal(MemoryPriorityLevel.BelowNormal, readBelowNormal);

        // Set to VeryLow (1)
        var setVeryLow = await manager.SetMemoryPriorityAsync(child.Process.Id, MemoryPriorityLevel.VeryLow);
        Assert.True(setVeryLow);
        var readVeryLow = await manager.TryGetMemoryPriorityAsync(child.Process.Id);
        Assert.Equal(MemoryPriorityLevel.VeryLow, readVeryLow);

        // Set back to Normal (5)
        var setNormal = await manager.SetMemoryPriorityAsync(child.Process.Id, MemoryPriorityLevel.Normal);
        Assert.True(setNormal);
        var readNormal = await manager.TryGetMemoryPriorityAsync(child.Process.Id);
        Assert.Equal(MemoryPriorityLevel.Normal, readNormal);
    }

    [Fact]
    public async Task RestoreIoPriorityIfUnchanged_AppliesRestoration()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        IProcessMemoryIoManager manager = new Win32ProcessMemoryIoManager();

        var identity = await manager.TryGetProcessIdentityAsync(child.Process.Id);
        Assert.NotNull(identity);

        await manager.SetIoPriorityAsync(child.Process.Id, IoPriorityLevel.Low);

        var result = await manager.RestoreIoPriorityIfUnchangedAsync(
            identity,
            expectedCurrentLevel: IoPriorityLevel.Low,
            originalLevel: IoPriorityLevel.Normal);

        Assert.Equal(ConditionalProcessUpdateResult.Applied, result);
        var current = await manager.TryGetIoPriorityAsync(child.Process.Id);
        Assert.Equal(IoPriorityLevel.Normal, current);
    }

    [Fact]
    public async Task RestoreIoPriorityIfUnchanged_DetectsCurrentValueMismatch()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        IProcessMemoryIoManager manager = new Win32ProcessMemoryIoManager();

        var identity = await manager.TryGetProcessIdentityAsync(child.Process.Id);
        Assert.NotNull(identity);

        await manager.SetIoPriorityAsync(child.Process.Id, IoPriorityLevel.Normal);

        var result = await manager.RestoreIoPriorityIfUnchangedAsync(
            identity,
            expectedCurrentLevel: IoPriorityLevel.Low, // Mismatch
            originalLevel: IoPriorityLevel.VeryLow);

        Assert.Equal(ConditionalProcessUpdateResult.CurrentValueMismatch, result);
    }

    [Fact]
    public async Task RestoreMemoryPriorityIfUnchanged_AppliesRestoration()
    {
        var executablePath = GetTestExecutablePath();
        using var child = SuspendedTestProcess.Start(executablePath);
        IProcessMemoryIoManager manager = new Win32ProcessMemoryIoManager();

        var identity = await manager.TryGetProcessIdentityAsync(child.Process.Id);
        Assert.NotNull(identity);

        await manager.SetMemoryPriorityAsync(child.Process.Id, MemoryPriorityLevel.Medium);

        var result = await manager.RestoreMemoryPriorityIfUnchangedAsync(
            identity,
            expectedCurrentLevel: MemoryPriorityLevel.Medium,
            originalLevel: MemoryPriorityLevel.Normal);

        Assert.Equal(ConditionalProcessUpdateResult.Applied, result);
        var current = await manager.TryGetMemoryPriorityAsync(child.Process.Id);
        Assert.Equal(MemoryPriorityLevel.Normal, current);
    }

    [Fact]
    public async Task SafetyPolicy_BlocksSelfProcess()
    {
        IProcessMemoryIoManager manager = new Win32ProcessMemoryIoManager();
        var selfPid = Environment.ProcessId;

        var ioResult = await manager.SetIoPriorityAsync(selfPid, IoPriorityLevel.Low);
        Assert.False(ioResult);

        var memResult = await manager.SetMemoryPriorityAsync(selfPid, MemoryPriorityLevel.Low);
        Assert.False(memResult);
    }
}
