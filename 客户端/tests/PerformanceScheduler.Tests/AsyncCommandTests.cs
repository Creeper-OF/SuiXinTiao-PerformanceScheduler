using PerformanceScheduler.App.Commands;

namespace PerformanceScheduler.Tests;

public sealed class AsyncCommandTests
{
    [Fact]
    public async Task Execute_AllowsOnlyOneConcurrentInvocation()
    {
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var command = new AsyncCommand(async () =>
        {
            Interlocked.Increment(ref executionCount);
            executionStarted.SetResult();
            await releaseExecution.Task;
            executionCompleted.SetResult();
        });

        var invocations = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => command.Execute(null)))
            .ToArray();
        await Task.WhenAll(invocations);
        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref executionCount));
        Assert.False(command.CanExecute(null));

        releaseExecution.SetResult();
        await executionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(20);

        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task Execute_AwaitsAsyncExceptionHandlerBeforeReleasingCommand()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncCommand(
            () => Task.FromException(new InvalidOperationException("command failed")),
            onException: async _ =>
            {
                handlerStarted.SetResult();
                await releaseHandler.Task;
                handlerCompleted.SetResult();
            });

        command.Execute(null);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(command.CanExecute(null));

        releaseHandler.SetResult();
        await handlerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(20);

        Assert.True(command.CanExecute(null));
    }
}
