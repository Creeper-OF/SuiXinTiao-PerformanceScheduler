namespace PerformanceScheduler.Core.Models;

/// <summary>
/// Raw file snapshot used to restore one configuration store after a failed import.
/// Keys are relative paths for directory-backed stores and an empty key for a single file.
/// </summary>
public sealed record ConfigurationStoreSnapshot
{
    public IReadOnlyDictionary<string, byte[]> Files { get; init; } =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
}
