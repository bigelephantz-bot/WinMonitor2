namespace WinMonitor.Core;

/// <summary>
/// Runs native calls on a dedicated thread so a driver call that never returns cannot pin the
/// caller.
///
/// PawnIO's user-mode library issues a synchronous IOCTL, and its pending path waits on the
/// request without a timeout. No managed mechanism can cancel a P/Invoke that is blocked in the
/// kernel: a Stopwatch measures it, a CancellationToken cannot reach it, and a Task wrapper only
/// moves which thread is stuck. The one thing that *is* achievable is deciding, from outside,
/// that the caller stops waiting — which is what this gate does.
///
/// The trade is explicit. On timeout the worker thread is lost: it is still inside the native
/// call and still owns everything the delegate touched — the PawnIO handle, an EC mutex, whatever
/// buffers were passed in. None of that may be reclaimed, so the gate is closed permanently
/// (<see cref="Wedged"/>) and every later call fails fast instead of queueing behind a thread that
/// will never come back. The owner is expected to report its feature as unavailable and to leave
/// the associated native objects alone for the rest of the process's life. That is the same rule
/// <see cref="PollThreadHandle"/> applies at shutdown: leaking a handle is recoverable, tearing
/// one down under a live native call is not.
/// </summary>
public sealed class NativeCallGate : IDisposable
{
    private const int WorkerExitJoinMs = 1000;

    private readonly object _sync = new();
    private readonly ManualResetEventSlim _hasWork = new(false);
    private readonly ManualResetEventSlim _workDone = new(false);
    private readonly PollThreadHandle _worker = new();
    private readonly string _name;

    private Action? _work;
    private volatile bool _wedged;
    private volatile bool _stopping;
    private bool _disposed;

    /// <param name="name">Used for the worker thread name and diagnostic breadcrumbs.</param>
    public NativeCallGate(string name) => _name = name;

    /// <summary>
    /// True once a call exceeded its timeout. The gate accepts no further work, and whatever the
    /// abandoned call owned must be treated as permanently in use.
    /// </summary>
    public bool Wedged => _wedged;

    /// <summary>
    /// Runs <paramref name="work"/> on the gate's thread and waits up to <paramref name="timeoutMs"/>.
    /// Returns false if the gate is closed or the call did not finish in time — in which case the
    /// delegate is still running and nothing it touched may be released. Exceptions thrown by the
    /// delegate are swallowed; report failure through the delegate's own state, not by throwing.
    /// </summary>
    public bool TryRun(Action work, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_sync)
        {
            if (_disposed || _wedged) return false;
            if (!EnsureWorkerLocked()) return false;

            _work = work;
            _workDone.Reset();
            _hasWork.Set();

            if (_workDone.Wait(Math.Max(0, timeoutMs))) return true;

            _wedged = true;
            Diag.Log("native", _name + " did not return within " + timeoutMs
                + " ms; gate closed and its native resources left in place");
            return false;
        }
    }

    private bool EnsureWorkerLocked()
    {
        if (_worker.IsRunning) return true;
        // A worker that was never observed leaving may still be inside a native call.
        if (_worker.Pending is not null) return false;
        // Normal priority, not the poll thread's BelowNormal: the caller is blocked on this thread
        // for the duration, so running it lower would be a self-inflicted priority inversion —
        // during startup, with the machine busy opening hardware, that showed up as real latency.
        return _worker.Start(Loop, "WinMonitor." + _name, ThreadPriority.Normal);
    }

    private void Loop()
    {
        while (true)
        {
            try { _hasWork.Wait(); } catch (ObjectDisposedException) { return; }
            if (_stopping) return;

            Action? work = _work;
            _work = null;
            // Reset before signalling completion so the next request cannot be missed.
            try { _hasWork.Reset(); } catch (ObjectDisposedException) { return; }
            try { work?.Invoke(); } catch { /* the caller reports failure from its own state */ }
            try { _workDone.Set(); } catch (ObjectDisposedException) { return; }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            try { _hasWork.Set(); } catch (ObjectDisposedException) { }
        }

        // The events may only be disposed once the worker is known to be out of Loop; a wedged
        // worker still references them, so they are left to process exit instead.
        if (_worker.Join(WorkerExitJoinMs))
        {
            _hasWork.Dispose();
            _workDone.Dispose();
            return;
        }
        Diag.Log("native", _name + " worker did not exit; leaving its handles to process exit");
    }
}
