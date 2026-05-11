namespace PerformanceScheduler.Core.Models;

public sealed record FocusedAppContext
{
    public required int ProcessId { get; init; }

    public required string ProcessName { get; init; }

    public string? ExecutablePath { get; init; }

    public required string WindowTitle { get; init; }

    public required nint WindowHandle { get; init; }

    public ProcessClassification Classification { get; init; } = ProcessClassification.Unknown;

    public PowerSourceMode PowerSourceMode { get; init; } = PowerSourceMode.Any;

    public string DisplayName => string.IsNullOrWhiteSpace(WindowTitle) ? ProcessName : WindowTitle;
}
