namespace PerformanceScheduler.Core.Models;

public sealed record ForegroundWindowSnapshot(
    nint WindowHandle,
    int ProcessId,
    string WindowTitle);
