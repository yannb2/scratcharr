namespace Scratcharr.Jobs;

/// <summary>
/// Pause all / Resume all for the job queue. While paused no job starts, and the running one waits where it is:
/// a copy between two 4 MB blocks, an unpack with 7-Zip stopped (SIGSTOP) until Resume all. The flag is saved, so
/// the queue stays paused after a restart.
/// </summary>
public sealed class PauseGate
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _open = Opened();

    public PauseGate(bool paused = false)
    {
        if (paused)
        {
            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public bool Paused
    {
        get
        {
            lock (_lock)
            {
                return !_open.Task.IsCompleted;
            }
        }
    }

    /// <summary>Raised with the new state after Pause or Resume changed it (not when it was already so).</summary>
    public event Action<bool>? Changed;

    public void Pause()
    {
        lock (_lock)
        {
            if (!_open.Task.IsCompleted)
            {
                return;
            }

            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Changed?.Invoke(true);
    }

    public void Resume()
    {
        TaskCompletionSource open;
        lock (_lock)
        {
            if (_open.Task.IsCompleted)
            {
                return;
            }

            open = _open;
        }

        open.TrySetResult();
        Changed?.Invoke(false);
    }

    /// <summary>Returns at once when not paused; otherwise when Resume all is pressed, or throws when cancelled.</summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        Task open;
        lock (_lock)
        {
            open = _open.Task;
        }

        return open.IsCompleted ? Task.CompletedTask : open.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource Opened()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.SetResult();
        return open;
    }
}
