using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class ConfigurationRecoverySafetyGateTests
{
    [Fact]
    public void MarkRecoveryRequired_BlocksResumeUntilRecoveryIsResolved()
    {
        var gate = new ConfigurationRecoverySafetyGate();

        gate.MarkRecoveryRequired();

        Assert.False(gate.CanResume);
        Assert.True(gate.RequiresManualAction);

        gate.MarkRecoveryResolved();

        Assert.True(gate.CanResume);
        Assert.False(gate.RequiresManualAction);
    }

    [Fact]
    public void NormalGate_AllowsResume()
    {
        var gate = new ConfigurationRecoverySafetyGate();

        Assert.True(gate.CanResume);
        Assert.False(gate.RequiresManualAction);
    }
}
