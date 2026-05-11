using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class DeviceCompatibilityEvaluatorTests
{
    [Fact]
    public void Evaluate_ReturnsSameHardwareWhenHardwareKeysMatch()
    {
        var evaluator = new DeviceCompatibilityEvaluator();
        var source = CreateDevice(model: "Legion 5", cpu: "Ryzen 7", gpu: "RTX 3060");
        var current = CreateDevice(model: "Legion 5", cpu: "Ryzen 7", gpu: "RTX 3060");

        var result = evaluator.Evaluate(source, current);

        Assert.Equal(DeviceCompatibilityLevel.SameHardware, result.Level);
        Assert.False(result.ShouldWarn);
    }

    [Fact]
    public void Evaluate_ReturnsSameMachineModelWhenModelMatchesButHardwareDiffers()
    {
        var evaluator = new DeviceCompatibilityEvaluator();
        var source = CreateDevice(model: "Legion 5", cpu: "Ryzen 7", gpu: "RTX 3060");
        var current = CreateDevice(model: "Legion 5", cpu: "Ryzen 5", gpu: "RTX 3050");

        var result = evaluator.Evaluate(source, current);

        Assert.Equal(DeviceCompatibilityLevel.SameMachineModel, result.Level);
        Assert.False(result.ShouldWarn);
    }

    [Fact]
    public void Evaluate_WarnsWhenDeviceDiffers()
    {
        var evaluator = new DeviceCompatibilityEvaluator();
        var source = CreateDevice(model: "Legion 5", cpu: "Ryzen 7", gpu: "RTX 3060");
        var current = CreateDevice(model: "ROG Zephyrus", cpu: "Core i7", gpu: "RTX 4070");

        var result = evaluator.Evaluate(source, current);

        Assert.Equal(DeviceCompatibilityLevel.DifferentDevice, result.Level);
        Assert.True(result.ShouldWarn);
    }

    private static DeviceFingerprint CreateDevice(string model, string cpu, string gpu) =>
        new()
        {
            Manufacturer = "Lenovo",
            Model = model,
            CpuName = cpu,
            GpuNames = new[] { gpu },
            TotalMemoryBytes = 16UL * 1024 * 1024 * 1024
        };
}
