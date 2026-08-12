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

        /// <summary>
        /// Bounded chart/sparkline ring, allocated only once a consumer actually asks for this
        /// sensor's history. At the default 3600-sample capacity each ring costs ~57 KB, so
        /// arming every discovered sensor eagerly would spend several MB on curves nobody plots.
        /// CSV export does not need it — the complete series lives in the disk spool.
        /// </summary>
        public RingBuffer<TimedValue>? History;
        public long HistoryVersion;
        public float LatestValue;
        public bool HasLatest;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _historyCapacity;
    private readonly SessionHistoryStore _sessionHistory = new();
    private bool _disposed;

    // Bumped by ResetPeaks. A backfill started before a reset carries the generation it was
    // scheduled under and is dropped on completion if it no longer matches, so cleared chart
    // history can never be repopulated with samples from before the reset.
    private long _resetGeneration;

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
                    entry = new Entry();
                    _entries[s.Id] = entry;
                }

                float v = s.Value.GetValueOrDefault();
                entry.Stats.Accept(v);
                entry.LatestValue = v;
                entry.HasLatest = true;
                // Only feed a ring that some consumer armed; unplotted sensors stay stats-only.
                if (entry.History is { } ring)
                {
                    ring.Add(new TimedValue(s.UtcTimestamp, v));
                    entry.HistoryVersion++;
                }
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
    /// Copied snapshot (oldest first) of every sample recorded for ONE sensor during this run.
    /// Unlike the chart ring this covers all sensors, because it reads the disk spool.
    ///
    /// COST: O(entire session), because the spool interleaves all sensors and must be scanned
    /// end to end to filter one of them. Intended for single-sensor inspection and tests only —
    /// never call it per descriptor in a loop; use <see cref="ExportTimeSeriesCsv"/>, which walks
    /// the spool exactly once for every column.
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

    /// <summary>
    /// True once the session spool reached its size cap: statistics stay live, but a CSV export
    /// no longer contains the newest samples. Surfaced in the Diagnostics tab.
    /// </summary>
    public bool SessionHistoryTruncated
    {
        get { lock (_gate) return _sessionHistory.Truncated; }
    }

    /// <summary>Bytes appended to the session spool so far (diagnostics only).</summary>
    public long SessionHistoryBytes
    {
        get { lock (_gate) return _sessionHistory.BytesWritten; }
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
        EnsureHistoryArmed(sensorId);
        lock (_gate)
        {
            Entry? entry = GetOrCreateEntryLocked(sensorId);
            if (entry is null)
                return new HistoryReadResult(-1, Array.Empty<TimedValue>());

            RingBuffer<TimedValue>? ring = entry.History;
            if (entry.HistoryVersion == knownVersion)
                return new HistoryReadResult(entry.HistoryVersion, null);

            TimedValue[] values = ring is { Count: > 0 }
                ? ring.ToArray()
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
        EnsureHistoryArmed(sensorId);
        lock (_gate)
        {
            Entry? entry = GetOrCreateEntryLocked(sensorId);
            if (entry is null || entry.History is not { } h) return 0;
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
        entry = new Entry();
        _entries[sensorId] = entry;
        return entry;
    }

    /// <summary>
    /// Allocates this sensor's chart ring the first time a consumer asks for its history, then
    /// backfills it from the disk spool on a worker so a chart opened mid-session shows what
    /// already happened instead of starting blank.
    ///
    /// Laziness is what keeps memory down: at the default capacity every armed ring costs ~57 KB,
    /// so arming all ~90 discovered sensors would spend several MB on curves nobody plots.
    ///
    /// The ring is published (empty) before returning, so this method is cheap and safe to call
    /// from the chart's UI tick. The spool scan is O(session) and reads from disk, so it must NOT
    /// run on the caller's thread — the chart and the tray both call in from the UI thread, and a
    /// long session would freeze the window. The scan lands via <see cref="ApplyBackfill"/>, which
    /// bumps the version so the chart picks it up on its next tick.
    /// </summary>
    private void EnsureHistoryArmed(string sensorId)
    {
        SessionHistoryReadSnapshot snapshot;
        long generation;
        lock (_gate)
        {
            if (_disposed) return;
            Entry? entry = GetOrCreateEntryLocked(sensorId);
            if (entry is null || entry.History is not null) return;

            // Publishing the ring here both starts collecting live samples immediately and makes
            // a concurrent call see the sensor as armed, so only one backfill is ever scheduled.
            entry.History = new RingBuffer<TimedValue>(_historyCapacity);
            snapshot = _sessionHistory.Capture();
            generation = _resetGeneration;
        }

        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (tracker, id, snap, gen) = ((StatsTracker, string, SessionHistoryReadSnapshot, long))state!;
            tracker.RunBackfill(id, snap, gen);
        }, (this, sensorId, snapshot, generation));
    }

    /// <summary>Scans the spool for one sensor and hands the result to <see cref="ApplyBackfill"/>.</summary>
    private void RunBackfill(string sensorId, SessionHistoryReadSnapshot snapshot, long generation)
    {
        // Collect straight into a ring: it discards the oldest sample in O(1), where trimming a
        // List from the front is O(n) per record and turns a long session into a quadratic scan.
        var backfill = new RingBuffer<TimedValue>(_historyCapacity);
        try
        {
            using (snapshot)
            {
                int sensorIndex = Array.IndexOf(snapshot.SensorIds, sensorId);
                if (sensorIndex < 0) return;
                foreach (SessionHistoryRecord record in snapshot.ReadRecords())
                {
                    if (record.SensorIndex != sensorIndex) continue;
                    backfill.Add(new TimedValue(new DateTime(record.UtcTicks, DateTimeKind.Utc), record.Value));
                }
            }
        }
        catch (Exception ex)
        {
            // An unreadable spool only costs the backfill; the ring still fills going forward.
            Diag.Log("history", "Chart history backfill failed for " + sensorId, ex);
            return;
        }

        if (backfill.Count > 0) ApplyBackfill(sensorId, backfill, generation);
    }

    /// <summary>
    /// Splices scanned history in front of whatever arrived while the scan ran. Discards the
    /// result when peaks were reset meanwhile: those samples predate the reset, and restoring
    /// them would contradict ResetPeaks having cleared the chart.
    /// </summary>
    private void ApplyBackfill(string sensorId, RingBuffer<TimedValue> backfill, long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _resetGeneration) return;
            if (!_entries.TryGetValue(sensorId, out Entry? entry) || entry.History is not { } live) return;

            // Rebuild as backfill (older) followed by samples collected during the scan, so the
            // ring stays in chronological order and overflow still drops the oldest.
            TimedValue[] arrivedDuringScan = live.ToArray();
            var rebuilt = new RingBuffer<TimedValue>(_historyCapacity);
            for (int i = 0; i < backfill.Count; i++) rebuilt.Add(backfill[i]);
            for (int i = 0; i < arrivedDuringScan.Length; i++) rebuilt.Add(arrivedDuringScan[i]);
            entry.History = rebuilt;
            entry.HistoryVersion++;
        }
    }

    /// <summary>
    /// Clears min/max/avg and bounded chart history. Complete session history is retained so
    /// resetting peaks never deletes measurements that should appear in a later CSV export.
    /// </summary>
    public void ResetPeaks()
    {
        lock (_gate)
        {
            // Invalidates any backfill already in flight; see _resetGeneration.
            _resetGeneration++;
            foreach (var entry in _entries.Values)
            {
                entry.Stats.Reset();
                // Armed rings are emptied, not released: a chart that is currently plotting this
                // sensor keeps its buffer and simply restarts from the next sample.
                entry.History?.Clear();
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
