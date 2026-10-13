namespace Wss.Transports.Backends.Linux;

internal static class TaskCancellation
{
    // Only the managed wait is cancelled. DBus operations continue; observe eventual faults.
    internal static async Task WaitAsync(this Task operation, CancellationToken token)
    {
        if (operation.IsCompleted || !token.CanBeCanceled)
        {
            await operation.ConfigureAwait(false);
            return;
        }
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(operation, cancelled.Task).ConfigureAwait(false) != operation)
            {
                _ = operation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                token.ThrowIfCancellationRequested();
            }
            await operation.ConfigureAwait(false);
        }
    }

    internal static async Task<T> WaitAsync<T>(this Task<T> operation, CancellationToken token)
    {
        await ((Task)operation).WaitAsync(token).ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }
}
