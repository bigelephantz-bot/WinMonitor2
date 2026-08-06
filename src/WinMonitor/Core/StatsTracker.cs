namespace WinMonitor.Core;

/// <summary>
/// A versioned chart-history read. <see cref="Values"/> is null when the caller's known
/// version is still current, avoiding a ring-buffer copy on an unchanged UI repaint.
/// </summary>
public readonly record struct HistoryReadResult(long Version, TimedValue[]? Values);

/// <summary>
/// Session min/max/avg statistics, disk-spooled complete history for CSV export, and a bounded
/// chart ring per sensor. Fed by SensorService on the polling thread; read by the UI, tray and
/// chart. A single lock guards all entries and the append-only spool.
/// </summary>
public sealed class StatsTracker : IDisposable
{
    // Hard cap on distinct tracked ids so a pathological descriptor stream cannot create
    // unbounded per-sensor containers.
    private const int MaxTrackedSensors = 512;

    private sealed class Entry
    {
        public readonly SessionStats Stats = new();
        public readonly RingBuffer<TimedValue> History;
        public long HistoryVersion;
        public float LatestValue;
        public bool HasLatest;

        public Entry(int historyCapacity)
        {
            History = new RingBuffer<TimedValue>(historyCapacity);
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _historyCapacity;
    private readonly SessionHistoryStore _sessionHistory = new();
    private bool _disposed;

    public StatsTracker(int historyCapacity = 3600)
    {
        _historyCapacity = Math.Max(1, historyCapacity);
    }

    /// <summary>Called on the polling background thread each tick. Skips empty/NaN samples.</summary>
    public void Accept(SensorSnapshot[] snapshots)
    {
        lock (_gate)
        {
            if (_disposed) return;
            for (int i = 0; i < snapshots.Length; i++)
            {
                var s = snapshots[i];
                if (!s.HasValue) continue;

                if (!_entries.TryGetValue(s.Id, out var entry))
                {
                    if (_entries.Count >= MaxTrackedSensors) continue;
                    entry = new Entry(_historyCapacity);
                    _entries[s.Id] = entry;
                }

                float v = s.Value.GetValueOrDefault();
                entry.Stats.Accept(v);
                entry.LatestValue = v;
                entry.HasLatest = true;
                entry.History.Add(new TimedValue(s.UtcTimestamp, v));
                entry.HistoryVersion++;
                _sessionHistory.Append(s.Id, s.UtcTimestamp, v);
            }
        }
    }

    /// <summary>
    /// Returns the live stats object (mutated on the polling thread) or null when the sensor
    /// has produced no data yet. Field reads may be momentarily stale — fine for display.
    /// </summary>
    public SessionStats? GetStats(string sensorId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(sensorId, out var e) && e.Stats.HasData ? e.Stats : null;
        }
    }

    /// <summary>Returns the most recent valid session value without copying the history.</summary>
    public float? GetLatestValue(string sensorId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sensorId, out Entry? entry) || !entry.HasLatest)
                return null;
            return entry.LatestValue;
        }
    }

    /// <summary>
    /// Copied snapshot (oldest first) of every sample recorded during this application run.
    /// Used by time-series CSV export; unlike the chart ring, it is populated for all sensors.
    /// </summary>
    public IReadOnlyList<TimedValue> GetHistory(string sensorId)
    {
        SessionHistoryReadSnapshot snapshot;
        lock (_gate)
        {
            if (!_entries.TryGetValue(sensorId, out Entry? entry) || !entry.HasLatest)
                return Array.Empty<TimedValue>();
            snapshot = _sessionHistory.Capture();
        }

        using (snapshot)
        {
            int sensorIndex = Array.IndexOf(snapshot.SensorIds, sensorId);
            if (sensorIndex < 0) return Array.Empty<TimedValue>();
            var values = new List<TimedValue>();
            foreach (SessionHistoryRecord record in snapshot.ReadRecords())
            {
                if (record.SensorIndex == sensorIndex)
                    values.Add(new TimedValue(new DateTime(record.UtcTicks, DateTimeKind.Utc), record.Value));
            }
            return values;
        }
    }

    /// <summary>Streams a stable session snapshot to CSV without materializing all histories.</summary>
    public string ExportTimeSeriesCsv(string path, IReadOnlyList<SensorDescriptor> descriptors)
    {
        SessionHistoryReadSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StatsTracker));
            snapshot = _sessionHistory.Capture();
        }
        using (snapshot)
            return HistoryLogger.ExportTimeSeriesCsv(path, descriptors, snapshot);
    }

    /// <summary>
    /// Returns a copied oldest-first history only after its version changed. Chart consumers pass
    /// the version retained from their previous read, so steady-state repaint ticks avoid both a
    /// lock-protected buffer copy and a new array. The first call should use <c>-1</c>.
    /// </summary>
    public HistoryReadResult GetHistoryIfChanged(string sensorId, long knownVersion)
    {
        lock (_gate)
        {
            Entry? entry = GetOrCreateEntryLocked(sensorId);
            if (entry is null)
                return new HistoryReadResult(-1, Array.Empty<TimedValue>());

            if (entry.HistoryVersion == knownVersion)
                return new HistoryReadResult(entry.HistoryVersion, null);

            TimedValue[] values = entry.History.Count > 0
                ? entry.History.ToArray()
                : Array.Empty<TimedValue>();
            return new HistoryReadResult(entry.HistoryVersion, values);
        }
    }

    /// <summary>
    /// Copies the last <paramref name="max"/> values (oldest→newest) into <paramref name="dest"/>
    /// without allocating for an already-tracked sensor. Returns the number written. Used by the
    /// tray sparkline so an idle, sparkline-enabled icon allocates nothing per tick.
    /// </summary>
    public int CopyRecentHistory(string sensorId, float[] dest, int max)
    {
        if (dest.Length == 0 || max <= 0) return 0;
        int want = Math.Min(max, dest.Length);
        lock (_gate)
        {
            Entry? entry = GetOrCreateEntryLocked(sensorId);
            if (entry is null) return 0;
            RingBuffer<TimedValue> h = entry.History;
            int n = Math.Min(want, h.Count);
            int start = h.Count - n;
            for (int i = 0; i < n; i++)
                dest[i] = h[start + i].Value;
            return n;
        }
    }

    /// <summary>
    /// Gets or creates a tracked entry. Caller holds <see cref="_gate"/>.
    /// </summary>
    private Entry? GetOrCreateEntryLocked(string sensorId)
    {
        if (_entries.TryGetValue(sensorId, out var entry)) return entry;
        if (_entries.Count >= MaxTrackedSensors) return null;
        entry = new Entry(_historyCapacity);
        _entries[sensorId] = entry;
        return entry;
    }

    /// <summary>
    /// Clears min/max/avg and bounded chart history. Complete session history is retained so
    /// resetting peaks never deletes measurements that should appear in a later CSV export.
    /// </summary>
    public void ResetPeaks()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Stats.Reset();
                entry.History.Clear();
                entry.HistoryVersion++;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _sessionHistory.Dispose();
        }
    }
}
