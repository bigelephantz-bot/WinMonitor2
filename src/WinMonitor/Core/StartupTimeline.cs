using System.Diagnostics;
using System.Text;

namespace WinMonitor.Core;

/// <summary>
/// Records how long each startup stage took, so cold-start work can be judged from measurements
/// instead of assumptions. The optimizations already applied (ReadyToRun, deferred WMI discovery,
/// skipping the duplicate first hardware sweep) were all reasoned about but never measured; this
/// is what makes the next such change checkable.
///
/// Stages are appended on the startup thread only, then emitted once as a single Diag line. The
/// cost is a few Stopwatch reads and one string build — nothing that would distort what it
/// measures. <see cref="ProcessStartOffsetMs"/> also captures time spent before Main ran (host
/// startup, JIT of the entry path), which is otherwise invisible.
/// </summary>
public static class StartupTimeline
{
    private const int MaxStages = 24;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly (string Name, long ElapsedMs)[] Stages = new (string, long)[MaxStages];
    private static int _count;
    private static long _lastMs;
    private static bool _emitted;

    /// <summary>Milliseconds between process creation and the timeline starting (host + JIT).</summary>
    public static long ProcessStartOffsetMs { get; private set; }

    public static void Begin()
    {
        try
        {
            // Process.StartTime is local time; both sides use local so the difference is valid.
            var started = Process.GetCurrentProcess().StartTime;
            long offset = (long)(DateTime.Now - started).TotalMilliseconds;
            ProcessStartOffsetMs = offset > 0 && offset < 120_000 ? offset : 0;
        }
        catch { ProcessStartOffsetMs = 0; }
    }

    /// <summary>Marks a stage complete. Durations are deltas, so callers just name what finished.</summary>
    public static void Mark(string stage)
    {
        if (_count >= MaxStages) return;
        long now = Clock.ElapsedMilliseconds;
        Stages[_count++] = (stage, now - _lastMs);
        _lastMs = now;
    }

    /// <summary>
    /// Writes the timeline to the diagnostic log. Safe to call more than once; only the first
    /// call emits, so an early-exit path cannot produce a partial duplicate.
    /// </summary>
    public static void Emit()
    {
        if (_emitted) return;
        _emitted = true;

        var sb = new StringBuilder(160);
        sb.Append("Startup ").Append(Clock.ElapsedMilliseconds).Append(" ms");
        if (ProcessStartOffsetMs > 0) sb.Append(" (+").Append(ProcessStartOffsetMs).Append(" ms before Main)");
        sb.Append(':');
        for (int i = 0; i < _count; i++)
            sb.Append(' ').Append(Stages[i].Name).Append('=').Append(Stages[i].ElapsedMs).Append("ms");
        Diag.Log("startup", sb.ToString());
    }

    /// <summary>Formatted timeline for the Diagnostics tab; empty before <see cref="Emit"/>.</summary>
    public static string Describe()
    {
        if (_count == 0) return string.Empty;
        var sb = new StringBuilder(120);
        for (int i = 0; i < _count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Stages[i].Name).Append(' ').Append(Stages[i].ElapsedMs).Append(" ms");
        }
        return sb.ToString();
    }

    /// <summary>Total milliseconds from the start of Main to the last mark.</summary>
    public static long TotalMs => _lastMs;
}
