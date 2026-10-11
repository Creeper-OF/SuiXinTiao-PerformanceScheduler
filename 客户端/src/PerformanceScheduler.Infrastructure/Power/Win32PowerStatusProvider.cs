using System.Runtime.InteropServices;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Infrastructure.Power;

public sealed class Win32PowerStatusProvider : IPowerStatusProvider
{
    public Task<PowerSourceMode> GetCurrentPowerSourceAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(PowerSourceMode.Any);
        }

        if (!GetSystemPowerStatus(out var status))
        {
            return Task.FromResult(PowerSourceMode.Any);
        }

        var mode = status.ACLineStatus switch
        {
            0 => PowerSourceMode.Battery,
            1 => PowerSourceMode.Ac,
            _ => PowerSourceMode.Any
        };

        return Task.FromResult(mode);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
