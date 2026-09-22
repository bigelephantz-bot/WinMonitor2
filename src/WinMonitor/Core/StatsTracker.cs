namespace WinMonitor.Core;

/// <summary>Values is null when the caller already holds the current complete ring copy.</summary>
public readonly record struct HistoryReadResult(long Version, TimedValue[]? Values);

/// <summary>
/// Live statistics plus a bounded chart cache and an append-only session spool. Immutable
/// session definitions preserve the meaning of retired/recalibrated sensors in CSV exports.
/// One lock protects metadata, current readings and appends; disk scans run outside that lock.
/// </summary>
public sealed class StatsTracker : IDisposable
{
    private const int MaxTrackedSensors = 512;
    private const int MaxSessionDefinitions = 4096;
    private const int RecordSize = sizeof(long) + sizeof(int) + sizeof(float);

    private sealed class Entry
    {
        public readonly SessionStats Stats = new();
        public required SessionSensorInfo Definition;
        public required int CatalogIndex;
        public bool MetadataKnown;
        public bool IsPresent = true;
        public bool CanSpool = true;
        public DateTime? LastObservedUtc;
        public float LatestValue;
        public bool HasLatest;
        public RingBuffer<TimedValue>? History;
        public long HistoryVersion;
        public long RevisionStartOffset;
    }

    private sealed record BackfillRequest(string SensorId, string HistoryId,
        RingBuffer<TimedValue> Target, long Generation, long FromOffset, long UntilOffset);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<SessionSensorInfo> _catalog = new();
    private readonly Dictionary<string, BackfillRequest> _pendingBackfills = new(StringComparer.Ordinal);
    private readonly int _historyCapacity;
    private readonly Action? _beforeHistoryScan;
    private readonly SessionHistoryStore _sessionHistory = new();
    private IReadOnlyList<SensorDescriptor>? _acceptedDescriptors;
    private bool _disposed;
    private bool _catalogTruncated;
    private bool _backfillWorkerQueued;
    private TaskCompletionSource? _backfillCompletion;
    private long _resetGeneration;
    private long _chartStartOffset;
    private long _backfillScanCount;
    private long _backfillRecordsRead;

    public StatsTracker(int historyCapacity = 3600, Action? beforeHistoryScan = null)
    {
        _historyCapacity = Math.Max(1, historyCapacity);
        _beforeHistoryScan = beforeHistoryScan;
    }

    /// <summary>
    /// Cold metadata registration. Existing measurement revisions are advanced only by Accept's
    /// matching poll batch, so a delayed UI callback cannot relabel a sample from an older batch.
    /// </summary>
    public void RegisterDescriptors(IReadOnlyList<SensorDescriptor> descriptors)
    {
        lock (_gate)
        {
            if (_disposed) return;
            for (int i = 0; i < descriptors.Count; i++)
            {
                Entry? entry = GetOrCreateEntryLocked(descriptors[i].Id);
                if (entry is not null && !entry.MetadataKnown)
                    DescribeEntryLocked(entry, descriptors[i]);
                else if (entry is not null)
                    UpdatePresentationLocked(entry, descriptors[i]);
            }
        }
    }

    private void UpdatePresentationLocked(Entry entry, SensorDescriptor descriptor)
    {
        SessionSensorInfo current = entry.Definition;
        // A stale UI callback can refresh a name, but cannot move the current measurement back
        // to an old quantity/calibration or rewrite a retired revision.
        if (current.Quantity != descriptor.Quantity
            || !string.Equals(current.MeasurementKey, descriptor.MeasurementKey, StringComparison.Ordinal)) return;
        string displayName = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? descriptor.Name : descriptor.DisplayName;
        if (current.Name == descriptor.Name && current.HardwareName == descriptor.HardwareName
            && current.DisplayName == displayName && current.Category == descriptor.Category) return;
        SessionSensorInfo updated = current with
        {
            HardwareName = descriptor.HardwareName,
            Name = descriptor.Name,
            DisplayName = displayName,
            Category = descriptor.Category,
        };
        entry.Definition = updated;
        if (entry.CanSpool && _catalog[entry.CatalogIndex].HistoryId == current.HistoryId)
            _catalog[entry.CatalogIndex] = updated;
    }

    /// <summary>Compatibility for producers without metadata; production passes descriptors.</summary>
    public void Accept(SensorSnapshot[] snapshots)
    {
        lock (_gate)
        {
            if (!_disposed) AcceptLocked(snapshots);
        }
    }

    /// <summary>
    /// Poll-thread entry point. Descriptor lists are immutable/replaced on definition changes;
    /// list identity makes steady-state metadata synchronization allocation-free and O(1).
    /// </summary>
    public void Accept(SensorSnapshot[] snapshots, IReadOnlyList<SensorDescriptor> descriptors)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!ReferenceEquals(_acceptedDescriptors, descriptors))
            {
                foreach (Entry entry in _entries.Values) entry.IsPresent = false;
                for (int i = 0; i < descriptors.Count; i++)
                {
                    SensorDescriptor descriptor = descriptors[i];
                    Entry? entry = GetOrCreateEntryLocked(descriptor.Id);
                    if (entry is null) continue;
                    entry.IsPresent = true;
                    DescribeEntryLocked(entry, descriptor);
                }
                foreach (Entry entry in _entries.Values)
                    if (!entry.IsPresent) entry.HasLatest = false;
                _acceptedDescriptors = descriptors;
            }
            AcceptLocked(snapshots);
        }
    }

    private void AcceptLocked(SensorSnapshot[] snapshots)
    {
        for (int i = 0; i < snapshots.Length; i++)
        {
            SensorSnapshot sample = snapshots[i];
            Entry? entry = GetOrCreateEntryLocked(sample.Id);
            if (entry is null) continue;
            entry.LastObservedUtc = sample.UtcTimestamp;
            float value = sample.Value.GetValueOrDefault();
            entry.HasLatest = sample.HasValue && float.IsFinite(value);
            if (!entry.HasLatest) continue;

            entry.LatestValue = value;
            entry.Stats.Accept(value);
            if (entry.History is { } ring)
            {
                ring.Add(new TimedValue(sample.UtcTimestamp, value));
                entry.HistoryVersion++;
            }
            if (entry.CanSpool)
                _sessionHistory.Append(entry.Definition.HistoryId, sample.UtcTimestamp, value);
        }
    }

    private void DescribeEntryLocked(Entry entry, SensorDescriptor descriptor)
    {
        SessionSensorInfo old = entry.Definition;
        string key = descriptor.MeasurementKey ?? string.Empty;
        if (entry.MetadataKnown && old.Quantity == descriptor.Quantity
            && string.Equals(old.MeasurementKey, key, StringComparison.Ordinal)) return;

        int revision = entry.MetadataKnown ? old.Revision + 1 : old.Revision;
        string historyId = entry.MetadataKnown
            ? descriptor.Id + "\u001f" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : old.HistoryId;
        var definition = new SessionSensorInfo(descriptor.Id, historyId,
            descriptor.HardwareName, descriptor.Name,
            string.IsNullOrWhiteSpace(descriptor.DisplayName) ? descriptor.Name : descriptor.DisplayName,
            descriptor.Category, descriptor.Quantity, key, revision);

        if (entry.MetadataKnown)
        {
            entry.Stats.Reset();
            entry.HasLatest = false;
            entry.LastObservedUtc = null;
            entry.History?.Clear();
            entry.HistoryVersion++;
            entry.RevisionStartOffset = _sessionHistory.BytesWritten;
            _pendingBackfills.Remove(descriptor.Id);
            if (_catalog.Count < MaxSessionDefinitions)
            {
                entry.CatalogIndex = _catalog.Count;
                _catalog.Add(definition);
            }
            else
            {
                entry.CanSpool = false;
                _catalogTruncated = true;
            }
        }
        else
        {
            // Legacy Accept callers may have written samples before supplying their metadata.
            _catalog[entry.CatalogIndex] = definition;
        }
        entry.Definition = definition;
        entry.MetadataKnown = true;
    }

    private Entry? GetOrCreateEntryLocked(string sensorId)
    {
        if (_disposed) return null;
        if (_entries.TryGetValue(sensorId, out Entry? entry)) return entry;
        if (_entries.Count >= MaxTrackedSensors || _catalog.Count >= MaxSessionDefinitions)
            return null;
        var definition = new SessionSensorInfo(sensorId, sensorId, string.Empty, sensorId, sensorId,
            SensorCategory.Other, SensorQuantity.Temperature, string.Empty, 1);
        entry = new Entry { Definition = definition, CatalogIndex = _catalog.Count };
        _catalog.Add(definition);
        _entries.Add(sensorId, entry);
        return entry;
    }

    public SessionStats? GetStats(string sensorId)
    {
        lock (_gate)
            return _entries.TryGetValue(sensorId, out Entry? entry) && entry.Stats.HasData ? entry.Stats : null;
    }

    public float? GetLatestValue(string sensorId)
    {
        lock (_gate)
            return _entries.TryGetValue(sensorId, out Entry? entry) && entry.IsPresent && entry.HasLatest
                ? entry.LatestValue : null;
    }

    public SensorObservationState? GetSensorState(string sensorId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sensorId, out Entry? entry)) return null;
            return new SensorObservationState(entry.IsPresent, entry.IsPresent && entry.HasLatest,
                entry.LastObservedUtc, entry.Definition.Revision, entry.Definition.Quantity,
                entry.Definition.MeasurementKey);
        }
    }

    /// <summary>Cold inspection/export view. Returned immutable definitions survive later edits.</summary>
    public SessionSensorInfo[] GetSessionCatalog()
    {
        lock (_gate) return _catalog.ToArray();
    }

    /// <summary>Complete disk history of the current measurement revision; not a per-tick API.</summary>
    public IReadOnlyList<TimedValue> GetHistory(string sensorId)
    {
        SessionHistoryReadSnapshot snapshot;
        string historyId;
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(sensorId, out Entry? entry))
                return Array.Empty<TimedValue>();
            historyId = entry.Definition.HistoryId;
            snapshot = _sessionHistory.Capture();
        }
        using (snapshot)
        {
            int sensorIndex = Array.IndexOf(snapshot.SensorIds, historyId);
            if (sensorIndex < 0) return Array.Empty<TimedValue>();
            var values = new List<TimedValue>();
            foreach (SessionHistoryRecord record in snapshot.ReadRecords())
                if (record.SensorIndex == sensorIndex)
                    values.Add(new TimedValue(new DateTime(record.UtcTicks, DateTimeKind.Utc), record.Value));
            return values;
        }
    }

    public bool SessionHistoryTruncated
    {
        get { lock (_gate) return _sessionHistory.Truncated || _catalogTruncated; }
    }

    public long SessionHistoryBytes
    {
        get { lock (_gate) return _sessionHistory.BytesWritten; }
    }

    /// <summary>Operation counters provide deterministic performance evidence without timing tests.</summary>
    public long HistoryBackfillScanCount => Interlocked.Read(ref _backfillScanCount);
    public long HistoryBackfillRecordsRead => Interlocked.Read(ref _backfillRecordsRead);

    public string ExportTimeSeriesCsv(string path)
    {
        SessionHistoryReadSnapshot snapshot;
        SessionSensorInfo[] catalog;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            snapshot = _sessionHistory.Capture();
            catalog = _catalog.ToArray();
        }
        using (snapshot) return HistoryLogger.ExportTimeSeriesCsv(path, catalog, snapshot);
    }

    /// <summary>
    /// Legacy callers supply missing metadata/current presentation names. The current descriptor
    /// list must never filter history or relabel an accumulated measurement's quantity/calibration.
    /// </summary>
    public string ExportTimeSeriesCsv(string path, IReadOnlyList<SensorDescriptor> descriptors)
    {
        RegisterDescriptors(descriptors);
        return ExportTimeSeriesCsv(path);
    }

    public HistoryReadResult GetHistoryIfChanged(string sensorId, long knownVersion)
    {
        lock (_gate)
        {
            Entry? entry = ArmHistoryLocked(sensorId);
            if (entry is null) return new HistoryReadResult(-1, Array.Empty<TimedValue>());
            if (entry.HistoryVersion == knownVersion) return new HistoryReadResult(knownVersion, null);
            return new HistoryReadResult(entry.HistoryVersion,
                entry.History is { Count: > 0 } ring ? ring.ToArray() : Array.Empty<TimedValue>());
        }
    }

    /// <summary>
    /// Copy only the visible window plus one predecessor for line continuity. Always restore the
    /// raw prefix: callers may convert it to display units in place. Reuse caller storage after
    /// its initial growth; Count excludes any stale tail. Cutoff movement also works without a tick.
    /// </summary>
    public HistoryWindowReadResult CopyVisibleWindow(string sensorId, DateTime fromUtc,
        ref TimedValue[] buffer, long knownVersion = -1)
    {
        lock (_gate)
        {
            Entry? entry = ArmHistoryLocked(sensorId);
            if (entry is null || entry.History is not { } ring)
                return new HistoryWindowReadResult(-1, 0);
            int lo = 0;
            int hi = ring.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (ring[mid].Utc < fromUtc) lo = mid + 1;
                else hi = mid;
            }
            int start = Math.Max(0, lo - 1);
            int count = ring.Count - start;
            if (buffer.Length < count)
                Array.Resize(ref buffer, Math.Min(_historyCapacity, Math.Max(count, Math.Max(16, buffer.Length * 2))));
            for (int i = 0; i < count; i++) buffer[i] = ring[start + i];
            return new HistoryWindowReadResult(entry.HistoryVersion, count);
        }
    }

    public int CopyRecentHistory(string sensorId, float[] dest, int max)
    {
        if (dest.Length == 0 || max <= 0) return 0;
        lock (_gate)
        {
            Entry? entry = ArmHistoryLocked(sensorId);
            if (entry?.History is not { } ring) return 0;
            int count = Math.Min(Math.Min(max, dest.Length), ring.Count);
            for (int i = 0; i < count; i++) dest[i] = ring[ring.Count - count + i].Value;
            return count;
        }
    }

    /// <summary>Arm a selection atomically, so all newly selected curves share one spool scan.</summary>
    public void ArmHistories(IReadOnlyList<string> sensorIds)
    {
        lock (_gate)
            for (int i = 0; i < sensorIds.Count; i++) ArmHistoryLocked(sensorIds[i]);
    }

    private Entry? ArmHistoryLocked(string sensorId)
    {
        Entry? entry = GetOrCreateEntryLocked(sensorId);
        if (entry is null || entry.History is not null) return entry;
        entry.History = new RingBuffer<TimedValue>(_historyCapacity);
        long from = Math.Max(_chartStartOffset, entry.RevisionStartOffset);
        long until = _sessionHistory.BytesWritten;
        if (until <= from) return entry;
        _pendingBackfills[sensorId] = new BackfillRequest(sensorId, entry.Definition.HistoryId,
            entry.History, _resetGeneration, from, until);
        if (!_backfillWorkerQueued)
        {
            _backfillWorkerQueued = true;
            _backfillCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ThreadPool.QueueUserWorkItem(static state => ((StatsTracker)state!).RunBackfills(), this);
        }
        return entry;
    }

    /// <summary>Async completion hook for callers/tests; never block the UI waiting for disk backfill.</summary>
    public Task WaitForHistoryBackfillAsync()
    {
        lock (_gate) return _backfillCompletion?.Task ?? Task.CompletedTask;
    }

    private void RunBackfills()
    {
        while (true)
        {
            BackfillRequest[] requests;
            SessionHistoryReadSnapshot snapshot;
            lock (_gate)
            {
                if (_disposed || _pendingBackfills.Count == 0)
                {
                    _backfillWorkerQueued = false;
                    _backfillCompletion?.TrySetResult();
                    return;
                }
                requests = new BackfillRequest[_pendingBackfills.Count];
                _pendingBackfills.Values.CopyTo(requests, 0);
                _pendingBackfills.Clear();
                snapshot = _sessionHistory.Capture();
            }

            try
            {
                using (snapshot) RunBackfillBatch(requests, snapshot);
            }
            catch (Exception ex)
            {
                Diag.Log("history", "Chart history batch backfill failed", ex);
            }
        }
    }

    private void RunBackfillBatch(BackfillRequest[] requests, SessionHistoryReadSnapshot snapshot)
    {
        var requestByIndex = new Dictionary<int, int>();
        var rings = new RingBuffer<TimedValue>[requests.Length];
        long from = long.MaxValue;
        long until = 0;
        for (int i = 0; i < requests.Length; i++)
        {
            int index = Array.IndexOf(snapshot.SensorIds, requests[i].HistoryId);
            if (index >= 0) requestByIndex[index] = i;
            rings[i] = new RingBuffer<TimedValue>(_historyCapacity);
            from = Math.Min(from, requests[i].FromOffset);
            until = Math.Max(until, requests[i].UntilOffset);
        }
        if (requestByIndex.Count == 0) return;
        _beforeHistoryScan?.Invoke();
        Interlocked.Increment(ref _backfillScanCount);
        long offset = from;
        long records = 0;
        foreach (SessionHistoryRecord record in snapshot.ReadRecords(from))
        {
            if (offset >= until) break;
            records++;
            if (requestByIndex.TryGetValue(record.SensorIndex, out int i)
                && offset >= requests[i].FromOffset && offset < requests[i].UntilOffset)
                rings[i].Add(new TimedValue(new DateTime(record.UtcTicks, DateTimeKind.Utc), record.Value));
            offset += RecordSize;
        }
        Interlocked.Add(ref _backfillRecordsRead, records);
        lock (_gate)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                BackfillRequest request = requests[i];
                if (_disposed || request.Generation != _resetGeneration
                    || !_entries.TryGetValue(request.SensorId, out Entry? entry)
                    || !ReferenceEquals(entry.History, request.Target)
                    || entry.Definition.HistoryId != request.HistoryId || rings[i].Count == 0) continue;
                RingBuffer<TimedValue> rebuilt = rings[i];
                for (int n = 0; n < request.Target.Count; n++) rebuilt.Add(request.Target[n]);
                entry.History = rebuilt;
                entry.HistoryVersion++;
            }
        }
    }

    /// <summary>Reset chart/statistics at a record boundary; retain every record for CSV export.</summary>
    public void ResetPeaks()
    {
        lock (_gate)
        {
            _resetGeneration++;
            _chartStartOffset = _sessionHistory.BytesWritten;
            _pendingBackfills.Clear();
            foreach (Entry entry in _entries.Values)
            {
                entry.Stats.Reset();
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
            _pendingBackfills.Clear();
            _sessionHistory.Dispose();
        }
    }
}
