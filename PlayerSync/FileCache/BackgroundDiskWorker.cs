using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MareSynchronos.FileCache;

/// <summary>
/// Runs download disk work on one thread that Windows treats as background I/O.
/// Download writes use the main queue. Maintenance (compacting, the cache scan, the CSV rewrite)
/// uses the idle queue and runs only when no download write is waiting.
/// Continuations are forced off this thread so a caller waiting on more disk work cannot deadlock the queue.
/// </summary>
public sealed class BackgroundDiskWorker : IDisposable
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    private readonly ILogger<BackgroundDiskWorker> _logger;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly ConcurrentQueue<Action> _idle = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Thread _thread;
    private bool _disposed;

    public BackgroundDiskWorker(ILogger<BackgroundDiskWorker> logger)
    {
        _logger = logger;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "PlayerSync Disk",
        };
        _thread.Start();
    }

    public bool IsOnDiskThread => ReferenceEquals(Thread.CurrentThread, _thread);

    public Task RunAsync(Action action, CancellationToken cancellationToken = default)
    {
        return RunAsync<object?>(() =>
        {
            action();
            return null;
        }, cancellationToken);
    }

    public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        if (IsOnDiskThread)
            return Task.FromResult(action());

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(BackgroundDiskWorker)));
        }

        Poke();
        return tcs.Task;
    }

    /// <summary>
    /// Queues work that runs only when the download queue is empty.
    /// Calling this from the disk thread enqueues and returns; it does not run inline and must not be awaited there.
    /// </summary>
    public Task RunIdleAsync(Action action, CancellationToken cancellationToken = default)
    {
        return RunIdleAsync<object?>(() =>
        {
            action();
            return null;
        }, cancellationToken);
    }

    public Task<T> RunIdleAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _idle.Enqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        Poke();
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.CompleteAdding();
        Poke();
        if (_thread.IsAlive && !IsOnDiskThread)
            _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Poke()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake is enough.
        }
    }

    private void ThreadMain()
    {
        var backgroundMode = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        if (!backgroundMode)
            _logger.LogWarning("Failed to enter background disk mode: Win32 error {error}", Marshal.GetLastWin32Error());
        else
            _logger.LogInformation("Download disk worker is running at background I/O priority");

        try
        {
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out var work))
                {
                    Invoke(work);
                    continue;
                }

                if (_idle.TryDequeue(out var idle))
                {
                    Invoke(idle);
                    continue;
                }

                _wake.Wait(TimeSpan.FromMilliseconds(250));
            }

            while (_queue.TryTake(out var work))
                Invoke(work);
        }
        finally
        {
            if (backgroundMode)
                SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
        }
    }

    private void Invoke(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background disk work item failed");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
}
