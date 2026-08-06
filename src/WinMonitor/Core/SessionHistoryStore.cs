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
/// </summary>
internal sealed class SessionHistoryStore : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "WinMonitor-session-" + Guid.NewGuid().ToString("N") + ".bin");
    private readonly Dictionary<string, int> _sensorIndexes = new(StringComparer.Ordinal);
    private readonly List<string> _sensorIds = new();
    private FileStream? _stream;
    private BinaryWriter? _writer;
    private bool _faulted;

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
        catch
        {
            CloseWriter();
            _faulted = true;
        }
    }

    public void Append(string sensorId, DateTime utcTimestamp, float value)
    {
        if (_faulted || _writer is null) return;
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
        }
        catch
        {
            _faulted = true;
            CloseWriter();
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
