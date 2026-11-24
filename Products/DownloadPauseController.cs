namespace MCPanel;

/// <summary>
/// Coordinates a reversible pause for the current product download without
/// cancelling the request or discarding its partially written files.
/// </summary>
public sealed class DownloadPauseController : IDisposable
{
    private readonly object _syncRoot = new();
    private TaskCompletionSource<bool>? _resumeSignal;
    private bool _isPaused;
    private bool _isDisposed;

    public bool IsPaused
    {
        get
        {
            lock (_syncRoot)
            {
                return _isPaused;
            }
        }
    }

    public bool Pause()
    {
        lock (_syncRoot)
        {
            if (_isDisposed || _isPaused)
            {
                return false;
            }

            _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isPaused = true;
            return true;
        }
    }

    public bool Resume()
    {
        TaskCompletionSource<bool>? signal;
        lock (_syncRoot)
        {
            if (_isDisposed || !_isPaused)
            {
                return false;
            }

            _isPaused = false;
            signal = _resumeSignal;
            _resumeSignal = null;
        }

        signal?.TrySetResult(true);
        return true;
    }

    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? resumeTask;
            lock (_syncRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_isDisposed || !_isPaused)
                {
                    return;
                }

                resumeTask = _resumeSignal?.Task;
            }

            if (resumeTask is null)
            {
                return;
            }

            await WaitWithCancellationAsync(resumeTask, cancellationToken).ConfigureAwait(false);
        }
    }

    public void WaitIfPaused(CancellationToken cancellationToken) =>
        WaitIfPausedAsync(cancellationToken).GetAwaiter().GetResult();

    public void Dispose()
    {
        TaskCompletionSource<bool>? signal;
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _isPaused = false;
            signal = _resumeSignal;
            _resumeSignal = null;
        }

        signal?.TrySetResult(true);
    }

    private static async Task WaitWithCancellationAsync(Task resumeTask, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await resumeTask.ConfigureAwait(false);
            return;
        }

        var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(state =>
               ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancellationSignal))
        {
            if (resumeTask != await Task.WhenAny(resumeTask, cancellationSignal.Task).ConfigureAwait(false))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        await resumeTask.ConfigureAwait(false);
    }
}
