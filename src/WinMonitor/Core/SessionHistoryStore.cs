using System.Text;

namespace WinMonitor.Core;

/// <summary>One fixed-width record from the session-history spool.</summary>
internal readonly record struct SessionHistoryRecord(long UtcTicks, int SensorIndex, float Value);

/// <summary>
/// Stable, independently readable view of the spool at one instant. The read handle is opened
/// before the live store can be disposed, so an export already in progress survives app shutdown.
/// </summary>
internal sealed class SessionHistoryReadSnapshot : IDisposable
{
    private const int RecordSize = sizeof(long) + sizeof(int) + sizeof(float);
    private readonly FileStream? _stream;
    private readonly long _length;

    public SessionHistoryReadSnapshot(FileStream? stream, long length, string[] sensorIds)
    {
        _stream = stream;
        _length = length;
        SensorIds = sensorIds;
    }

    public string[] SensorIds { get; }

    public IEnumerable<SessionHistoryRecord> ReadRecords()
    {
        if (_stream is null) yield break;

        _stream.Position = 0;
        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        while (_stream.Position + RecordSize <= _length)
        {
            long ticks = reader.ReadInt64();
            int sensorIndex = reader.ReadInt32();
            float value = reader.ReadSingle();
            yield return new SessionHistoryRecord(ticks, sensorIndex, value);
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
    }
}

/// <summary>
/// Append-only, process-local spool for the complete time series. Keeping it on disk preserves
/// the export contract without retaining an ever-growing <see cref="List{T}"/> for every sensor.
///
/// Growth is bounded two ways. <see cref="MaxBytes"/> stops appending once the spool reaches its
/// cap (a 24/7 tray session would otherwise write ~40 MB/day at 60 sensors and 2 s polling), and
/// <see cref="SweepOrphans"/> removes spools abandoned by a previous run. Deletion in
/// <see cref="Dispose"/> only covers a clean exit; a force-kill, crash, or power loss leaves the
/// file behind, so the sweep is what actually keeps %TEMP% from accumulating them.
///
/// IMPLICIT CONTRACT: the CSV exporter groups records into one row per distinct timestamp, which
/// is only correct because every sensor sampled in a poll tick is appended with that tick's single
/// timestamp. If a future change gives a subset of sensors its own cadence with its own timestamps
/// (EC read throttling currently avoids this by emitting only on read ticks), one logical tick
/// would split across several sparse rows.
/// </summary>
internal sealed class SessionHistoryStore : IDisposable
{
    private const string FilePrefix = "WinMonitor-session-";
    private const string FileExtension = ".bin";

    /// <summary>Hard cap for one session's spool. Reaching it stops appending, never throws.</summary>
    private const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>Orphans younger than this may belong to a concurrently starting instance.</summary>
    private static readonly TimeSpan OrphanMinAge = TimeSpan.FromHours(6);

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), FilePrefix + Guid.NewGuid().ToString("N") + FileExtension);
    private readonly Dictionary<string, int> _sensorIndexes = new(StringComparer.Ordinal);
    private readonly List<string> _sensorIds = new();
    private FileStream? _stream;
    private BinaryWriter? _writer;
    private bool _faulted;
    private long _bytesWritten;

    public SessionHistoryStore()
    {
        try
        {
            _stream = new FileStream(
                _path, FileMode.CreateNew, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                FileOptions.SequentialScan);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
        }
        catch (Exception ex)
        {
            CloseWriter();
            _faulted = true;
            Diag.Log("history", "Session spool unavailable; CSV export will be empty", ex);
        }
    }

    /// <summary>True once the cap stopped the spool: exports are missing their newest samples.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Bytes appended so far (diagnostics only).</summary>
    public long BytesWritten => _bytesWritten;

    /// <summary>
    /// Deletes session spools left behind by runs that did not exit cleanly. Called once at
    /// startup on a background thread; failures are ignored (another instance may hold a file).
    /// </summary>
    public static void SweepOrphans()
    {
        int deleted = 0;
        try
        {
            DateTime cutoff = DateTime.UtcNow - OrphanMinAge;
            foreach (string file in Directory.EnumerateFiles(Path.GetTempPath(), FilePrefix + "*" + FileExtension))
            {
                try
                {
                    // A live instance keeps its spool open; the delete simply fails and we skip it.
                    if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch { /* in use by another instance, or already gone */ }
            }
        }
        catch { /* the temp directory may be unreadable; nothing to recover */ }

        if (deleted > 0) Diag.Log("history", "Swept " + deleted + " orphaned session spool(s)");
    }

    public void Append(string sensorId, DateTime utcTimestamp, float value)
    {
        if (_faulted || _writer is null) return;
        if (_bytesWritten >= MaxBytes)
        {
            if (!Truncated)
            {
                Truncated = true;
                Diag.Log("history", "Session spool hit its " + (MaxBytes / (1024 * 1024))
                    + " MB cap; further samples are not exported");
                // Flush what we have so an export still sees every retained record.
                try { _writer.Flush(); } catch { }
            }
            return;
        }

        try
        {
            if (!_sensorIndexes.TryGetValue(sensorId, out int sensorIndex))
            {
                sensorIndex = _sensorIds.Count;
                _sensorIndexes[sensorId] = sensorIndex;
                _sensorIds.Add(sensorId);
            }

            _writer.Write(utcTimestamp.ToUniversalTime().Ticks);
            _writer.Write(sensorIndex);
            _writer.Write(value);
            _bytesWritten += sizeof(long) + sizeof(int) + sizeof(float);
        }
        catch (Exception ex)
        {
            _faulted = true;
            CloseWriter();
            Diag.Log("history", "Session spool write failed; history export stops here", ex);
        }
    }

    public SessionHistoryReadSnapshot Capture()
    {
        if (_stream is null || _writer is null)
            return new SessionHistoryReadSnapshot(null, 0, _sensorIds.ToArray());

        try
        {
            _writer.Flush();
            _stream.Flush(flushToDisk: false);
            long length = _stream.Position;
            var reader = new FileStream(
                _path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                FileOptions.SequentialScan);
            return new SessionHistoryReadSnapshot(reader, length, _sensorIds.ToArray());
        }
        catch
        {
            return new SessionHistoryReadSnapshot(null, 0, _sensorIds.ToArray());
        }
    }

    public void Dispose()
    {
        CloseWriter();
        try { File.Delete(_path); } catch { }
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }
}
