namespace WinMonitor.Core;

/// <summary>
/// A versioned chart-history read. <see cref="Values"/> is null when the caller's known
/// version is still current, avoiding a ring-buffer copy on an unchanged UI repaint.
/// </summary>
public readonly record struct HistoryReadResult(long Version, TimedValue[]? Values);

/// <summary>
/// Session min/max/avg statistics, complete session history for CSV export, and a lazily
/// allocated bounded chart ring per sensor. Fed by SensorService on the polling thread;
/// read by the UI, tray and chart. A single lock guards all entries.
/// </summary>
public sealed class StatsTracker
{
    // Hard cap on distinct tracked ids so a pathological descriptor stream cannot create
    // unbounded per-sensor containers. Session histories intentionally grow for the duration
    // of this process because the CSV contract is "every value since application start";
    // bounded chart rings remain lazy and independent.
    private const int MaxTrackedSensors = 512;

    private sealed class Entry
    {
        public readonly SessionStats Stats = new();
        /// <summary>All valid samples since application start; retained for time-series export.</summary>
        public readonly List<TimedValue> SessionHistory = new();
        /// <summary>Null until a consumer first requests this sensor's history.</summary>
        public RingBuffer<TimedValue>? History;
        /// <summary>Incremented whenever the lazily-created history changes.</summary>
        public long HistoryVersion;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _historyCapacity;

    public StatsTracker(int historyCapacity = 3600)
    {
        _historyCapacity = Math.Max(1, historyCapacity);
    }

    /// <summary>Called on the polling background thread each tick. Skips empty/NaN samples.</summary>
    public void Accept(SensorSnapshot[] snapshots)
    {
        lock (_gate)
        {
            for (int i = 0; i < snapshots.Length; i++)
            {
                var s = snapshots[i];
                if (!s.HasValue) continue;

                if (!_entries.TryGetValue(s.Id, out var entry))
                {
                    if (_entries.Count >= MaxTrackedSensors) continue;
                    entry = new Entry();
                    _entries[s.Id] = entry;
                }

                float v = s.Value.GetValueOrDefault();
                entry.Stats.Accept(v);
                entry.SessionHistory.Add(new TimedValue(s.UtcTimestamp, v));
                if (entry.History is { } history)
                {
                    history.Add(new TimedValue(s.UtcTimestamp, v));
                    entry.HistoryVersion++;
                }
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
            if (!_entries.TryGetValue(sensorId, out Entry? entry) || entry.SessionHistory.Count == 0)
                return null;
            return entry.SessionHistory[^1].Value;
        }
    }

    /// <summary>
    /// Copied snapshot (oldest first) of every sample recorded during this application run.
    /// Used by time-series CSV export; unlike the chart ring, it is populated for all sensors.
    /// </summary>
    public IReadOnlyList<TimedValue> GetHistory(string sensorId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sensorId, out Entry? entry) || entry.SessionHistory.Count == 0)
                return Array.Empty<TimedValue>();
            return entry.SessionHistory.ToArray();
        }
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

            RingBuffer<TimedValue> history = entry.History ??= new RingBuffer<TimedValue>(_historyCapacity);
            if (entry.HistoryVersion == knownVersion)
                return new HistoryReadResult(entry.HistoryVersion, null);

            TimedValue[] values = history.Count > 0 ? history.ToArray() : Array.Empty<TimedValue>();
            return new HistoryReadResult(entry.HistoryVersion, values);
        }
    }

    /// <summary>
    /// Copies the last <paramref name="max"/> values (oldest→newest) into <paramref name="dest"/>
    /// without allocating (after the sensor's ring is armed by the first call). Returns the
    /// number written. Used by the tray sparkline so an idle, sparkline-enabled icon allocates
    /// nothing per tick.
    /// </summary>
    public int CopyRecentHistory(string sensorId, float[] dest, int max)
    {
        if (dest.Length == 0 || max <= 0) return 0;
        int want = Math.Min(max, dest.Length);
        lock (_gate)
        {
            var h = GetOrCreateHistoryLocked(sensorId);
            if (h is null) return 0;
            int n = Math.Min(want, h.Count);
            int start = h.Count - n;
            for (int i = 0; i < n; i++)
                dest[i] = h[start + i].Value;
            return n;
        }
    }

    /// <summary>
    /// Returns the sensor's history ring, allocating it (and its entry) on first request so
    /// only sensors actually charted or sparklined ever pay for the buffer. Caller holds _gate.
    /// Null only when the tracked-sensor cap is hit.
    /// </summary>
    private RingBuffer<TimedValue>? GetOrCreateHistoryLocked(string sensorId)
    {
        Entry? entry = GetOrCreateEntryLocked(sensorId);
        return entry is null ? null : entry.History ??= new RingBuffer<TimedValue>(_historyCapacity);
    }

    /// <summary>Gets or creates a tracked entry. Caller holds <see cref="_gate"/>.</summary>
    private Entry? GetOrCreateEntryLocked(string sensorId)
    {
        if (_entries.TryGetValue(sensorId, out var entry)) return entry;
        if (_entries.Count >= MaxTrackedSensors) return null;
        entry = new Entry();
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
                if (entry.History is not null)
                {
                    entry.History.Clear(); // once allocated, rings are kept and just emptied
                    entry.HistoryVersion++;
                }
            }
        }
    }
}
