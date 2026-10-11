using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

public sealed class DeviceCompatibilityEvaluator
{
    public DeviceCompatibilityResult Evaluate(DeviceFingerprint? sourceDevice, DeviceFingerprint currentDevice)
    {
        if (sourceDevice is null || !sourceDevice.HasMachineModel || !currentDevice.HasMachineModel)
        {
            return new DeviceCompatibilityResult
            {
                Level = DeviceCompatibilityLevel.Unknown,
                ReasonKey = "DeviceCompatibility.Unknown"
            };
        }

        if (!string.IsNullOrWhiteSpace(sourceDevice.HardwareKey) &&
            string.Equals(sourceDevice.HardwareKey, currentDevice.HardwareKey, StringComparison.Ordinal))
        {
            return new DeviceCompatibilityResult
            {
                Level = DeviceCompatibilityLevel.SameHardware,
                ReasonKey = "DeviceCompatibility.SameHardware"
            };
        }

        if (string.Equals(sourceDevice.MachineModelKey, currentDevice.MachineModelKey, StringComparison.Ordinal))
        {
            return new DeviceCompatibilityResult
            {
                Level = DeviceCompatibilityLevel.SameMachineModel,
                ReasonKey = "DeviceCompatibility.SameMachineModel"
            };
        }

        return new DeviceCompatibilityResult
        {
            Level = DeviceCompatibilityLevel.DifferentDevice,
            ReasonKey = "DeviceCompatibility.DifferentDevice"
        };
    }
}
