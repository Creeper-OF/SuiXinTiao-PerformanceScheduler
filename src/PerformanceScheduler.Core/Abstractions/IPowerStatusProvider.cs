using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Abstractions;

public interface IPowerStatusProvider
{
    Task<PowerSourceMode> GetCurrentPowerSourceAsync(CancellationToken cancellationToken = default);
}
