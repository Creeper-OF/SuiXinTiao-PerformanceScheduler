using System.Windows.Input;

namespace PerformanceScheduler.App.Commands;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Func<Exception, Task>? _onException;
    private int _isRunning;

    public AsyncCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Func<Exception, Task>? onException = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onException = onException;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _isRunning) == 0 && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if ((_canExecute?.Invoke() ?? true) == false ||
            Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return;
        }

        RaiseCanExecuteChanged();

        try
        {
            await _execute();
        }
        catch (Exception exception)
        {
            if (_onException is null)
            {
                throw;
            }

            await _onException(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
