using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class StartupRecoverySafetyGateTests
{
    [Fact]
    public void Apply_GpuRecoveryFailureCannotBeClearedByProcessRollback()
    {
        var gate = new StartupRecoverySafetyGate();
        gate.Apply(new StartupRecoveryResult { GpuRecoveryFailed = true });

        gate.MarkRollbackResolved();

        Assert.False(gate.CanResume);
        Assert.True(gate.RequiresManualAction);
    }

    [Fact]
    public void GpuRecoveryResolution_OnlyClearsGpuProtection()
    {
        var gate = new StartupRecoverySafetyGate();
        gate.Apply(new StartupRecoveryResult { GpuRecoveryFailed = true, RollbackPending = true });

        gate.MarkGpuRecoveryResolved();

        Assert.False(gate.CanResume);
        gate.MarkRollbackResolved();
        Assert.True(gate.CanResume);
    }

    [Fact]
    public void GpuRecoveryResolution_DoesNotClearUnreadableStartupState()
    {
        var gate = new StartupRecoverySafetyGate();
        gate.Apply(new StartupRecoveryResult { GpuRecoveryFailed = true, StartupStateReadFailed = true });

        gate.MarkGpuRecoveryResolved();
        gate.MarkRollbackResolved();

        Assert.False(gate.CanResume);
    }

    [Fact]
    public void Apply_PendingRollbackBlocksResumeUntilRollbackIsResolved()
    {
        var gate = new StartupRecoverySafetyGate();

        gate.Apply(new StartupRecoveryResult { RollbackPending = true });

        Assert.False(gate.CanResume);
        Assert.True(gate.RequiresManualAction);

        gate.MarkRollbackResolved();

        Assert.True(gate.CanResume);
        Assert.False(gate.RequiresManualAction);
    }

    [Fact]
    public void Apply_UnreadableStateRemainsBlockedAfterRollbackResolution()
    {
        var gate = new StartupRecoverySafetyGate();

        gate.Apply(new StartupRecoveryResult
        {
            StartupStateReadFailed = true,
            RollbackFailed = true
        });

        gate.MarkRollbackResolved();

        Assert.False(gate.CanResume);
        Assert.True(gate.RequiresManualAction);
    }

    [Fact]
    public void Apply_ProfileDisableFailureBlocksResumeUntilStateIsRepaired()
    {
        var gate = new StartupRecoverySafetyGate();

        gate.Apply(new StartupRecoveryResult { ProfileDisableFailed = true });
        gate.MarkRollbackResolved();

        Assert.False(gate.CanResume);
        Assert.True(gate.RequiresManualAction);
    }

    [Fact]
    public void Apply_NormalRecoveryAllowsResume()
    {
        var gate = new StartupRecoverySafetyGate();

        gate.Apply(new StartupRecoveryResult
        {
            PreviousSessionRecovered = true,
            RollbackApplied = true
        });

        Assert.True(gate.CanResume);
        Assert.False(gate.RequiresManualAction);
    }

    [Fact]
    public void MarkStateRepairRequired_BlocksResumeUntilTheNextCleanSession()
    {
        var gate = new StartupRecoverySafetyGate();

        gate.MarkStateRepairRequired();
        gate.MarkRollbackResolved();

        Assert.False(gate.CanResume);
        Assert.True(gate.RequiresManualAction);
    }

    [Fact]
    public void MarkRollbackRequired_BlocksResumeUntilRollbackIsResolved()
    {
        var gate = new StartupRecoverySafetyGate();

        gate.MarkRollbackRequired();

        Assert.False(gate.CanResume);
        Assert.True(gate.RequiresManualAction);

        gate.MarkRollbackResolved();

        Assert.True(gate.CanResume);
    }
}
