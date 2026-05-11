namespace PerformanceScheduler.Core.Models;

public sealed record DeviceFingerprint
{
    public string Manufacturer { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string CpuName { get; init; } = string.Empty;

    public IReadOnlyList<string> GpuNames { get; init; } = Array.Empty<string>();

    public ulong? TotalMemoryBytes { get; init; }

    public string MachineModelKey =>
        NormalizeKey($"{Manufacturer}|{Model}");

    public string HardwareKey =>
        NormalizeKey($"{Manufacturer}|{Model}|{CpuName}|{string.Join("|", GpuNames)}|{TotalMemoryBytes}");

    public bool HasMachineModel =>
        !string.IsNullOrWhiteSpace(Manufacturer) ||
        !string.IsNullOrWhiteSpace(Model);

    private static string NormalizeKey(string value) =>
        string.Join(
            " ",
            value
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim()
            .ToUpperInvariant();
}
