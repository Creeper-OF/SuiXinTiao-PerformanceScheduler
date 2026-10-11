using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace PerformanceScheduler.App.Services;

/// <summary>
/// Prevents two scheduler processes from sharing the same live rollback state.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> HeldNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Mutex _mutex;
    private readonly string _name;
    private int _disposed;

    private SingleInstanceGuard(Mutex mutex, string name)
    {
        _mutex = mutex;
        _name = name;
    }

    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        guard = null;
        Mutex? mutex = null;

        if (!HeldNames.TryAdd(name, 0))
        {
            return false;
        }

        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                if (!mutex.WaitOne(TimeSpan.Zero))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous process terminated without releasing the mutex.
                // The current process owns it after this exception.
            }

            guard = new SingleInstanceGuard(mutex, name);
            mutex = null;
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return false;
        }
        finally
        {
            mutex?.Dispose();
            if (guard is null)
            {
                HeldNames.TryRemove(name, out _);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The OS already released an abandoned mutex; disposal is complete.
        }
        finally
        {
            _mutex.Dispose();
            HeldNames.TryRemove(_name, out _);
        }
    }
}
