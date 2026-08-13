namespace WinMonitor.Core;

/// <summary>
/// Owns one background worker thread and the single fact shutdown depends on: whether that thread
/// has actually left.
///
/// The distinction matters because a poll tick wedged inside a native call — LibreHardwareMonitor's
/// <c>Update()</c>, an EC register read, a hardware rescan — still owns the Computer, the embedded
/// controller and the wait handles it was given. Releasing those after a Join that merely *timed
/// out* is a use-after-free in native code, not a leak. This handle therefore forgets its thread
/// only when the exit was observed, and refuses to start a second worker on top of a live one.
/// </summary>
public sealed class PollThreadHandle
{
    private Thread? _thread;

    /// <summary>True while the worker exists and is still executing.</summary>
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>
    /// The worker still being accounted for, or null once its exit was observed. Non-null after a
    /// timed-out <see cref="Join"/> — that is exactly the state in which nothing may be released.
    /// </summary>
    public Thread? Pending => _thread;

    /// <summary>
    /// Starts a worker. Returns false — without starting anything — while a previous worker is
    /// still alive, so a stop that timed out can never be followed by two threads polling the same
    /// hardware.
    /// </summary>
    public bool Start(ThreadStart body, string name, ThreadPriority priority)
    {
        // A worker that exited after a timed-out Join is safe to forget now.
        if (_thread is { IsAlive: false }) _thread = null;
        if (_thread is not null) return false;

        var thread = new Thread(body)
        {
            IsBackground = true,
            Name = name,
            Priority = priority,
        };
        _thread = thread;
        thread.Start();
        return true;
    }

    /// <summary>
    /// Waits up to <paramref name="joinMs"/> for the worker to leave. Returns true only when it
    /// did; until then the caller must not release anything the worker can touch. Returns false
    /// when called from the worker itself — a thread cannot join itself, and it is unwinding anyway.
    /// </summary>
    public bool Join(int joinMs)
    {
        Thread? thread = _thread;
        if (thread is null) return true;
        if (ReferenceEquals(thread, Thread.CurrentThread)) return false;

        bool exited;
        // Thread-state races during shutdown are non-fatal, but they are not evidence of an exit:
        // treat anything other than a confirmed Join as "still running" and keep the reference.
        try { exited = thread.Join(joinMs); }
        catch { exited = false; }
        if (exited) _thread = null;
        return exited;
    }
}
