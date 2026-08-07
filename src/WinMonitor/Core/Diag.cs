using System.Globalization;
using System.Text;

namespace WinMonitor.Core;

/// <summary>
/// Size-capped rolling breadcrumb log for lifecycle and failure events.
///
/// The codebase deliberately swallows most exceptions so one flaky device can never stop
/// monitoring. That resilience previously left nothing on disk to explain a degraded session:
/// a backend that silently fell back to WMI-only, an EC handle that never recovered after
/// resume, or a poll loop failing every tick all looked identical to "some sensors are
/// missing". This log keeps the silent-catch behavior and only adds a trace, so a problem
/// that reproduces once — hours ago, across a sleep cycle — can still be diagnosed.
///
/// Every method is failure-tolerant: logging must never become a new failure source.
/// Volume is intentionally low (lifecycle transitions only, never per-tick), so the log is
/// always on; <see cref="MaxBytes"/> plus a single rolled generation bound it absolutely.
/// </summary>
public static class Diag
{
    private const long MaxBytes = 256 * 1024;
    private const string FileName = "winmonitor.log";

    private static readonly object Gate = new();
    private static readonly StringBuilder Builder = new(256);
    private static string? _path;
    private static string? _rolledPath;

    /// <summary>Full path of the active log, or null before <see cref="Initialize"/> succeeds.</summary>
    public static string? LogPath
    {
        get { lock (Gate) return _path; }
    }

    /// <summary>
    /// Points the log at <paramref name="directory"/>. Safe to call more than once; a failure
    /// here simply leaves logging disabled for the session.
    /// </summary>
    public static void Initialize(string directory)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, FileName);
                _rolledPath = _path + ".1";
            }
            catch
            {
                _path = null;
                _rolledPath = null;
            }
        }
    }

    public static void Log(string category, string message)
    {
        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                RollIfNeededLocked();
                Builder.Clear();
                Builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                Builder.Append(" [").Append(category).Append("] ");
                Builder.Append(message.Replace('\r', ' ').Replace('\n', ' '));
                File.AppendAllText(_path, Builder.ToString() + Environment.NewLine);
            }
            catch { /* a diagnostic log must never break the caller */ }
        }
    }

    /// <summary>Logs an exception's type and message; the stack trace stays in crash.log.</summary>
    public static void Log(string category, string message, Exception? ex)
    {
        if (ex is null) { Log(category, message); return; }
        Log(category, message + " -- " + ex.GetType().Name + ": " + ex.Message);
    }

    /// <summary>Truncates the active log once it exceeds the cap, keeping one rolled generation.</summary>
    private static void RollIfNeededLocked()
    {
        if (_path is null || _rolledPath is null) return;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes) return;
            File.Copy(_path, _rolledPath, overwrite: true);
            File.WriteAllText(_path, string.Empty);
        }
        catch { /* keep appending to an oversized file rather than losing the trail */ }
    }
}
