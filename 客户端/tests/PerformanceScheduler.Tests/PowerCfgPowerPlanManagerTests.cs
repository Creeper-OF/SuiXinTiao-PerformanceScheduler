using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Power;

namespace PerformanceScheduler.Tests;

public sealed class PowerCfgPowerPlanManagerTests
{
    [Theory]
    [InlineData("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *", "Balanced")]
    [InlineData("电源方案 GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (平衡) *", "平衡")]
    public void ParsePowerPlans_HandlesLocalizedLabels(string line, string expectedName)
    {
        var plan = Assert.Single(PowerCfgPowerPlanManager.ParsePowerPlans(line));

        Assert.Equal(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), plan.SchemeGuid);
        Assert.Equal(expectedName, plan.Name);
        Assert.True(plan.IsActive);
    }

    [Fact]
    public void ParsePowerPlans_PreservesParenthesesInCustomPlanNames()
    {
        var line = "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Gaming (AC + Battery)) *";

        var plan = Assert.Single(PowerCfgPowerPlanManager.ParsePowerPlans(line));

        Assert.Equal("Gaming (AC + Battery)", plan.Name);
    }

    [Fact]
    public void ParsePowerSettingValue_ReadsAcAndBatteryValuesFromLocalizedOutput()
    {
        var output = string.Join(
            Environment.NewLine,
            "当前交流电源设置索引: 0x00000064",
            "当前直流电源设置索引: 0x00000050");

        var acValue = PowerCfgPowerPlanManager.ParsePowerSettingValue(output, PowerSourceMode.Ac);
        var batteryValue = PowerCfgPowerPlanManager.ParsePowerSettingValue(output, PowerSourceMode.Battery);

        Assert.Equal(100, acValue);
        Assert.Equal(80, batteryValue);
    }

    [Fact]
    public void ParsePowerSettingValue_DoesNotGuessWhenPowerSourceIsAny()
    {
        var output = string.Join(
            Environment.NewLine,
            "当前交流电源设置索引: 0x00000064",
            "当前直流电源设置索引: 0x00000050");

        Assert.Null(PowerCfgPowerPlanManager.ParsePowerSettingValue(output, PowerSourceMode.Any));
    }

    [Fact]
    public async Task GetAdvancedSettingsAsync_DoesNotQueryPowerCfgForUnknownPowerSource()
    {
        var manager = new PowerCfgPowerPlanManager();

        var state = await manager.GetAdvancedSettingsAsync(Guid.NewGuid(), PowerSourceMode.Any);

        Assert.Null(state);
    }

    [Fact]
    public async Task ApplyAdvancedSettingsAsync_RefusesToWriteUnknownPowerSource()
    {
        var manager = new PowerCfgPowerPlanManager();

        var applied = await manager.ApplyAdvancedSettingsAsync(
            Guid.NewGuid(),
            PowerSourceMode.Any,
            new PowerPlanAdvancedPreference { ProcessorMaxStatePercent = 80 });

        Assert.False(applied);
    }

    [Fact]
    public async Task RestoreAdvancedSettingsAsync_RefusesToWriteUnknownPowerSource()
    {
        var manager = new PowerCfgPowerPlanManager();

        var restored = await manager.RestoreAdvancedSettingsAsync(
            new PowerPlanAdvancedState
            {
                SchemeGuid = Guid.NewGuid(),
                PowerSourceMode = PowerSourceMode.Any,
                ProcessorMaxStatePercent = 80
            });

        Assert.False(restored);
    }
}
