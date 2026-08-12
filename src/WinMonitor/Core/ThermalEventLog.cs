using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WinMonitor.Core;

public enum ThermalEventKind
{
    Alert,             // a sensor crossed its red threshold for the sustain time
    ThrottleStarted,
    ThrottleEnded,
}

/// <summary>One recorded thermal event. Times are UTC; formatting happens at display time.</summary>
public readonly record struct ThermalEvent(
    DateTime UtcTime,
    ThermalEventKind Kind,
    string SensorName,
    float Value,
    string ForegroundProcess);

/// <summary>
/// A bounded, in-memory history of alerts and throttle transitions, each tagged with the
/// foreground application at the moment it happened.
///
/// Alerts were previously fire-and-forget: a toast appeared and the event was gone. That answers
/// "it is hot now" but not "why did it get hot, and what was I running?" — which is the question
/// a user actually has afterwards. Recording the foreground process turns an isolated spike into
/// something attributable.
///
/// The ring is small and the whole class is lock-guarded; events arrive at human frequency (an
/// alert has to survive a sustain filter first), never per poll tick.
/// </summary>
public static class ThermalEventLog
{
    private const int Capacity = 200;

    private static readonly object Gate = new();
    private static readonly ThermalEvent[] Events = new ThermalEvent[Capacity];
    private static int _start;
    private static int _count;

    public static int Count
    {
        get { lock (Gate) return _count; }
    }

    /// <summary>
    /// Records an event, capturing the foreground process itself. Call on the UI thread: reading
    /// the foreground window from the polling thread would race the desktop's own state.
    /// </summary>
    public static void Record(ThermalEventKind kind, string sensorName, float value)
    {
        var entry = new ThermalEvent(DateTime.UtcNow, kind, sensorName, value, ForegroundProcessName());
        lock (Gate)
        {
            if (_count < Capacity)
            {
                Events[(_start + _count) % Capacity] = entry;
                _count++;
            }
            else
            {
                Events[_start] = entry;
                _start = (_start + 1) % Capacity;
            }
        }

        // Also breadcrumb it: the ring dies with the process, the log survives a crash or sleep.
        Diag.Log("thermal", kind + " " + sensorName + " " +
            value.ToString("0.#", CultureInfo.InvariantCulture) +
            (entry.ForegroundProcess.Length > 0 ? " while " + entry.ForegroundProcess : ""));
    }

    /// <summary>Newest-first copy of the recorded events, capped at <paramref name="max"/>.</summary>
    public static ThermalEvent[] Recent(int max)
    {
        lock (Gate)
        {
            int take = Math.Min(max, _count);
            var result = new ThermalEvent[take];
            for (int i = 0; i < take; i++)
                result[i] = Events[(_start + _count - 1 - i) % Capacity];
            return result;
        }
    }

    public static void Clear()
    {
        lock (Gate) { _start = 0; _count = 0; }
    }

    /// <summary>
    /// Process name owning the foreground window, or "" when it cannot be determined. Every
    /// failure is swallowed: this is contextual colour, never a reason to lose the event.
    /// </summary>
    private static string ForegroundProcessName()
    {
        try
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return "";
            _ = GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0) return "";
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return ""; }
    }

    /// <summary>Renders the newest events as lines for the Diagnostics view.</summary>
    public static string Describe(int max)
    {
        ThermalEvent[] recent = Recent(max);
        if (recent.Length == 0) return "";

        var sb = new StringBuilder(recent.Length * 64);
        foreach (ThermalEvent e in recent)
        {
            sb.Append(e.UtcTime.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            sb.Append("  ").Append(e.Kind switch
            {
                ThermalEventKind.ThrottleStarted => "THROTTLE>",
                ThermalEventKind.ThrottleEnded => "THROTTLE<",
                _ => "ALERT",
            });
            sb.Append("  ").Append(e.SensorName);
            if (e.Kind == ThermalEventKind.Alert)
                sb.Append(' ').Append(e.Value.ToString("0.#", CultureInfo.CurrentCulture));
            if (e.ForegroundProcess.Length > 0)
                sb.Append("  [").Append(e.ForegroundProcess).Append(']');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
