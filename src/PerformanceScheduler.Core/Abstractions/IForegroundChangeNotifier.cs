namespace PerformanceScheduler.Core.Abstractions;

public interface IForegroundChangeNotifier : IDisposable
{
    event EventHandler? ForegroundChanged;

    bool IsRunning { get; }

    bool Start();

    void Stop();
}
