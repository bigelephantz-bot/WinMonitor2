using WinMonitor.Core;

static class HistoryLifecycleTests
{
    public static void RetiredSensorExport()
    {
        string path = TempCsv();
        DateTime time = Utc();
        var a = Descriptor("A", SensorQuantity.Temperature, "", "Original A");
        var b = Descriptor("B", SensorQuantity.Fan, "", "Original B");
        using var tracker = new StatsTracker(4);
        try
        {
            tracker.Accept([Sample("A", 30, time), Sample("B", 3000, time)], [a, b]);
            SessionSensorInfo[] frozen = tracker.GetSessionCatalog();
            a.DisplayName = "Renamed A";
            b.DisplayName = "Changed after retirement";
            tracker.Accept([Sample("A", 31, time.AddSeconds(1))], [a]);
            tracker.ExportTimeSeriesCsv(path, [a]);
            string[] lines = File.ReadAllLines(path);
            Check.Equal(3, lines[0].Split(',').Length,
                "A removed descriptor must retain its session CSV column.");
            Check.True(lines[0].Contains("Renamed A", StringComparison.Ordinal)
                && lines[0].Contains("Original B", StringComparison.Ordinal)
                && !lines[0].Contains("Changed after retirement", StringComparison.Ordinal),
                "Explicit current-label refresh must preserve retired labels and avoid mutable descriptor aliases.");
            Check.Equal("3000", lines[1].Split(',')[2], "Retired sensor records must remain exportable.");
            Check.Equal(string.Empty, lines[2].Split(',')[2], "A retired sensor must have no invented later value.");
            Check.Equal("Original A", frozen[0].DisplayName, "Catalog snapshots must remain immutable.");
            Check.True(tracker.GetSensorState("B") is { IsPresent: false, IsAvailable: false },
                "Retirement must be visible in the central sensor state.");
            Check.Equal(1, tracker.GetHistory("B").Count,
                "Loss of a latest value must not hide previously recorded history.");
        }
        finally { File.Delete(path); }
    }

    public static void MeasurementRevisions()
    {
        string path = TempCsv();
        DateTime time = Utc();
        using var tracker = new StatsTracker(4);
        const string id = "/ec/reg/B0/Word";
        try
        {
            tracker.Accept([Sample(id, 3000, time)], [Descriptor(id, SensorQuantity.Fan, "scale=1")]);
            tracker.ArmHistories([id]);
            Wait(tracker);
            tracker.Accept([Sample(id, 40, time.AddSeconds(1))],
                [Descriptor(id, SensorQuantity.Temperature, "scale=1")]);
            Check.Equal(40f, tracker.GetStats(id)!.Min, "A quantity revision must reset live statistics.");
            Check.Equal(1, tracker.GetHistoryIfChanged(id, -1).Values!.Length,
                "A quantity revision must clear the previous unit's chart ring.");
            tracker.RegisterDescriptors([Descriptor(id, SensorQuantity.Fan, "scale=1")]);
            Check.True(tracker.GetSensorState(id) is { Revision: 2, Quantity: SensorQuantity.Temperature },
                "A late UI registration must never roll an accepted measurement revision backward.");
            tracker.Accept([Sample(id, 80, time.AddSeconds(2))],
                [Descriptor(id, SensorQuantity.Temperature, "scale=2")]);
            tracker.Accept([Sample(id, 82, time.AddSeconds(3))],
                [Descriptor(id, SensorQuantity.Temperature, "scale=2", "Cosmetic rename")]);
            Check.Equal(2L, tracker.GetStats(id)!.Count,
                "A calibration revision resets statistics; a cosmetic rename must not.");
            Check.True(tracker.GetSensorState(id) is { Revision: 3, Quantity: SensorQuantity.Temperature },
                "Quantity and calibration changes need separate session definitions.");
            Check.Equal(2, tracker.GetHistory(id).Count,
                "Single-sensor history must not combine different measurement definitions.");
            tracker.ExportTimeSeriesCsv(path);
            string[] lines = File.ReadAllLines(path);
            Check.Equal(4, lines[0].Split(',').Length, "Three definitions must produce three separate columns.");
            Check.True(lines[0].Contains("[Fan]", StringComparison.Ordinal)
                && lines[0].Contains("[Temperature] [v2]", StringComparison.Ordinal)
                && lines[0].Contains("[Temperature] [v3]", StringComparison.Ordinal),
                "Export metadata must identify the original units and each later calibration.");
            Check.Equal("3000,,", string.Join(',', lines[1].Split(',').Skip(1)), "Old RPM belongs only to v1.");
            Check.Equal(",40,", string.Join(',', lines[2].Split(',').Skip(1)), "First temperature belongs only to v2.");
            Check.Equal(",,80", string.Join(',', lines[3].Split(',').Skip(1)), "Recalibrated temperature belongs only to v3.");

            using var boolean = new StatsTracker();
            boolean.Accept([Sample(WellKnown.ThrottleSensorId, 0, time)],
                [Descriptor(WellKnown.ThrottleSensorId, SensorQuantity.Level, "v1")]);
            boolean.Accept([Sample(WellKnown.ThrottleSensorId, 1, time.AddSeconds(1))],
                [Descriptor(WellKnown.ThrottleSensorId, SensorQuantity.Level, "v2")]);
            boolean.ExportTimeSeriesCsv(path);
            string csv = File.ReadAllText(path);
            Check.True(csv.Contains("False", StringComparison.Ordinal) && csv.Contains("True", StringComparison.Ordinal),
                "Revisioned history ids must retain the source sensor's Boolean formatting.");
        }
        finally { File.Delete(path); }
    }

    public static void ResetAndObservationState()
    {
        const string id = "reset";
        DateTime time = Utc();
        var descriptors = new[] { Descriptor(id, SensorQuantity.Temperature) };
        using var tracker = new StatsTracker(8);
        tracker.Accept([Sample(id, 10, time)], descriptors);
        tracker.ResetPeaks();
        tracker.Accept([Sample(id, 20, time)], descriptors);
        tracker.ArmHistories([id]);
        Wait(tracker);
        TimedValue[] values = tracker.GetHistoryIfChanged(id, -1).Values!;
        Check.Equal(1, values.Length, "Late arming must exclude every record preceding reset, even equal timestamps.");
        Check.Equal(20f, values[0].Value, "Reset boundary is record position, not wall-clock time.");
        Check.Equal(2, tracker.GetHistory(id).Count, "Peak reset must preserve whole-session export history.");
        tracker.Accept(Array.Empty<SensorSnapshot>(), descriptors);
        Check.True(tracker.GetLatestValue(id) == 20f, "Sparse omission is not an observed failure.");
        tracker.Accept([Sample(id, null, time.AddSeconds(1))], descriptors);
        Check.True(tracker.GetLatestValue(id) is null, "An explicit unavailable sample must clear the latest value.");
        Check.True(tracker.GetSensorState(id) is { IsPresent: true, IsAvailable: false } state
            && state.LastObservedUtc == time.AddSeconds(1), "Failed observations must keep their timestamp.");
        tracker.Accept([Sample(id, float.PositiveInfinity, time.AddSeconds(2))], descriptors);
        Check.True(tracker.GetLatestValue(id) is null, "Infinite input must not contaminate statistics or chart scale.");
        Check.Equal(1L, tracker.GetStats(id)!.Count, "Unavailable values must not enter statistics.");

        // Hold a real scan after capture, reset, then release it: no scheduler timing assumptions.
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var raced = new StatsTracker(8, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release history scan.");
        });
        raced.Accept([Sample(id, 10, time)], descriptors);
        raced.ArmHistories([id]);
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(10)), "Backfill must reach the deterministic pause.");
            raced.ResetPeaks();
            raced.Accept([Sample(id, 30, time)], descriptors);
        }
        finally { release.Set(); }
        Wait(raced);
        values = raced.GetHistoryIfChanged(id, -1).Values!;
        Check.Equal(1, values.Length, "A pre-reset backfill must not repopulate the post-reset chart.");
        Check.Equal(30f, values[0].Value, "Samples collected after reset must survive a late scan.");
    }

    public static void BatchedHistoryScan()
    {
        const int sensors = 20;
        const int ticks = 200;
        using var tracker = new StatsTracker(64);
        var descriptors = new SensorDescriptor[sensors];
        var ids = new string[sensors];
        var samples = new SensorSnapshot[sensors];
        for (int i = 0; i < sensors; i++)
        {
            ids[i] = "batch-" + i;
            descriptors[i] = Descriptor(ids[i], SensorQuantity.Temperature);
        }
        DateTime time = Utc();
        for (int t = 0; t < ticks; t++)
        {
            for (int i = 0; i < sensors; i++) samples[i] = Sample(ids[i], t + i, time.AddSeconds(t));
            tracker.Accept(samples, descriptors);
        }
        tracker.ArmHistories(ids);
        Wait(tracker);
        Check.Equal(1L, tracker.HistoryBackfillScanCount, "A selected group must share exactly one spool scan.");
        Check.Equal((long)(sensors * ticks), tracker.HistoryBackfillRecordsRead,
            "Batch backfill must visit the spool once, not once per sensor.");
        for (int i = 0; i < sensors; i++)
        {
            TimedValue[] values = tracker.GetHistoryIfChanged(ids[i], -1).Values!;
            Check.Equal(64, values.Length, "Each selected ring keeps its own capacity.");
            Check.Equal((float)(ticks - 1 + i), values[^1].Value, "Batch routing must preserve every sensor's newest value.");
        }
        Console.WriteLine($"  History backfill: {sensors} curves, {tracker.HistoryBackfillScanCount} scan, "
            + $"{tracker.HistoryBackfillRecordsRead} records (previous per-curve path: {sensors * sensors * ticks}).");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var live = new StatsTracker(8, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release batch scan.");
        });
        var liveDescriptors = new[] { Descriptor("live", SensorQuantity.Temperature) };
        live.Accept([Sample("live", 1, time)], liveDescriptors);
        live.ArmHistories(["live"]);
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(10)), "The captured spool must reach the pause.");
            live.Accept([Sample("live", 2, time)], liveDescriptors);
        }
        finally { release.Set(); }
        Wait(live);
        TimedValue[] merged = live.GetHistoryIfChanged("live", -1).Values!;
        Check.Equal(2, merged.Length, "A live arrival while scanning must be merged exactly once.");
        Check.Equal(1f, merged[0].Value, "Backfill must precede live arrivals.");
        Check.Equal(2f, merged[1].Value, "Equal timestamps do not make a live arrival disappear.");
    }

    public static void VisibleWindowReuse()
    {
        const string id = "window";
        const int capacity = 3600;
        DateTime time = Utc();
        using var tracker = new StatsTracker(capacity);
        tracker.ArmHistories([id]);
        var descriptors = new[] { Descriptor(id, SensorQuantity.Temperature) };
        var samples = new SensorSnapshot[1];
        for (int i = 0; i < capacity; i++)
        {
            samples[0] = Sample(id, i, time.AddSeconds(i));
            tracker.Accept(samples, descriptors);
        }
        TimedValue[] buffer = [];
        DateTime cutoff = time.AddSeconds(3540);
        HistoryWindowReadResult first = tracker.CopyVisibleWindow(id, cutoff, ref buffer);
        Check.Equal(61, first.Count, "A one-minute window includes only visible samples and one boundary point.");
        Check.Equal(3539f, buffer[0].Value, "Preserve the predecessor so the left edge of the curve remains continuous.");
        TimedValue[] originalBuffer = buffer;
        buffer[0] = new TimedValue(buffer[0].Utc, -999);
        HistoryWindowReadResult shifted = tracker.CopyVisibleWindow(id, cutoff.AddSeconds(10), ref buffer, first.Version);
        Check.Equal(51, shifted.Count, "Advancing cutoff must trim the valid prefix even when no poll arrived.");
        Check.Equal(3549f, buffer[0].Value, "Every copy must restore raw values after display-unit conversion.");
        Check.True(ReferenceEquals(originalBuffer, buffer), "A narrower window must reuse caller storage.");
        for (int i = 0; i < 100; i++) tracker.CopyVisibleWindow(id, cutoff, ref buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) tracker.CopyVisibleWindow(id, cutoff, ref buffer);
        long windowBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.Equal(0L, windowBytes, "An already-sized visible-window buffer must allocate zero bytes per refresh.");
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) tracker.GetHistoryIfChanged(id, -1);
        long fullCopyBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.True(fullCopyBytes > 0, "The baseline must measure actual full-ring array allocations.");
        Console.WriteLine($"  Chart window: {first.Count}/{capacity} values copied; "
            + $"{windowBytes} B / 1000 reused reads; baseline full rings {fullCopyBytes} B / 100 reads.");
        tracker.ResetPeaks();
        HistoryWindowReadResult empty = tracker.CopyVisibleWindow(id, cutoff, ref buffer);
        Check.Equal(0, empty.Count, "Reset must return an empty valid prefix despite stale values in reusable storage.");
        Check.True(ReferenceEquals(originalBuffer, buffer), "Reset must not discard caller storage.");
    }

    private static SensorDescriptor Descriptor(string id, SensorQuantity quantity, string key = "", string name = "Sensor")
        => new()
        {
            Id = id, HardwareName = "Test", Name = name, DisplayName = name,
            Category = SensorCategory.Other, Quantity = quantity, MeasurementKey = key,
        };

    private static SensorSnapshot Sample(string id, float? value, DateTime utc)
        => new() { Id = id, Value = value, UtcTimestamp = utc };

    private static DateTime Utc() => new(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
    private static string TempCsv() => Path.Combine(Path.GetTempPath(), "WinMonitor-history-test-" + Guid.NewGuid().ToString("N") + ".csv");

    private static void Wait(StatsTracker tracker)
        => Check.True(tracker.WaitForHistoryBackfillAsync().Wait(TimeSpan.FromSeconds(10)),
            "The bounded history scan must finish during the test.");
}
