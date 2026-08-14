using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;
using WinMonitor.Tray;
using WinMonitor.UI;

var tests = new (string Name, Action Run)[]
{
    (nameof(StatsTrackerHistoryTests), StatsTrackerHistoryTests),
    (nameof(ConfigSanitizationTests), ConfigSanitizationTests),
    (nameof(SuppressedStorageReferenceTests), SuppressedStorageReferenceTests),
    (nameof(ProfileAlertTests), ProfileAlertTests),
    (nameof(HistoryLayoutFingerprintTests), HistoryLayoutFingerprintTests),
    (nameof(HistoryLoggerDisableTests), HistoryLoggerDisableTests),
    (nameof(CsvExportTests), CsvExportTests),
    (nameof(TrayUnitFormattingTests), TrayUnitFormattingTests),
    (nameof(IntelThermalStatusDecodeTests), IntelThermalStatusDecodeTests),
    (nameof(TrayIconLayoutTests), TrayIconLayoutTests),
    (nameof(LocalizationCoverageTests), LocalizationCoverageTests),
    (nameof(AmbiguousSensorNameTests), AmbiguousSensorNameTests),
    (nameof(EcSensorComputeTests), EcSensorComputeTests),
    (nameof(ConfigMigrationTests), ConfigMigrationTests),
    (nameof(LoggingCompletenessTests), LoggingCompletenessTests),
    (nameof(PollThreadLifetimeTests), PollThreadLifetimeTests),
    (nameof(TrayLatestValuePruneTests), TrayLatestValuePruneTests),
    (nameof(NativeCallGateTests), NativeCallGateTests),
    (nameof(EcMutexFailClosedTests), EcMutexFailClosedTests),
    (nameof(IconHandleOwnershipTests), IconHandleOwnershipTests),
    (nameof(ConfigBackupFailureTests), ConfigBackupFailureTests),
    (nameof(MigrationChainTests), MigrationChainTests),
    (nameof(CsvLayoutIdentityTests), CsvLayoutIdentityTests),
    (nameof(CsvTornFileTests), CsvTornFileTests),
    (nameof(SessionSpoolFaultTests), SessionSpoolFaultTests),
    (nameof(CsvExportAtomicityTests), CsvExportAtomicityTests),
    (nameof(TrayIconMergeTests), TrayIconMergeTests),
    (nameof(WindowIconOwnershipTests), WindowIconOwnershipTests),
    (nameof(TrayCanvasDpiTests), TrayCanvasDpiTests),
    (nameof(EcTimeoutBufferOwnershipTests), EcTimeoutBufferOwnershipTests),
};

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} regression checks passed.");
return failures == 0 ? 0 : 1;

static void StatsTrackerHistoryTests()
{
    const string sensorId = "/cpu/package/temperature/0";
    using var tracker = new StatsTracker(historyCapacity: 3);

    HistoryReadResult initial = tracker.GetHistoryIfChanged(sensorId, -1);
    Check.True(initial.Values is { Length: 0 }, "First history read should arm an empty ring.");

    DateTime firstTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    tracker.Accept(new[] { Sample(sensorId, 50f, firstTime) });
    HistoryReadResult first = tracker.GetHistoryIfChanged(sensorId, initial.Version);
    Check.True(first.Values is { Length: 1 }, "A new sample should produce one copied history value.");
    Check.Equal(50f, first.Values![0].Value, "History should retain the sample value.");

    HistoryReadResult unchanged = tracker.GetHistoryIfChanged(sensorId, first.Version);
    Check.True(unchanged.Values is null, "An unchanged history version must avoid allocating a copy.");

    tracker.Accept(new[] { Sample(sensorId, 51f, firstTime.AddSeconds(1)) });
    HistoryReadResult second = tracker.GetHistoryIfChanged(sensorId, first.Version);
    Check.True(second.Values is { Length: 2 }, "A later sample should advance the version and history.");

    tracker.ResetPeaks();
    HistoryReadResult reset = tracker.GetHistoryIfChanged(sensorId, second.Version);
    Check.True(reset.Values is { Length: 0 }, "Reset must invalidate and clear chart history.");

    IReadOnlyList<TimedValue> session = tracker.GetHistory(sensorId);
    Check.Equal(2, session.Count, "CSV session history must retain samples after resetting peaks.");

    const string unchartedId = "/cpu/package/power/0";
    tracker.Accept(new[] { Sample(unchartedId, 12f, firstTime.AddSeconds(2)) });
    Check.Equal(1, tracker.GetHistory(unchartedId).Count,
        "CSV history must record sensors even when no chart requested them first.");

    // A chart opened mid-session is backfilled from the spool. The scan reads from disk and is
    // O(session), so it runs on a worker rather than the caller's thread: doing it inline froze
    // the UI, because the chart and tray both call in from the UI thread. The first read
    // therefore arms an empty ring and the history lands shortly after, version-bumped so the
    // chart picks it up on its next tick.
    const string lateChartId = "/cpu/package/frequency/0";
    for (int i = 0; i < 4; i++)
        tracker.Accept(new[] { Sample(lateChartId, 1000f + i, firstTime.AddSeconds(3 + i)) });

    HistoryReadResult late = tracker.GetHistoryIfChanged(lateChartId, -1);
    Check.True(late.Values is not null, "Arming a late chart must return immediately, not block on the scan.");

    for (int waited = 0; waited < 100 && late.Values!.Length == 0; waited++)
    {
        Thread.Sleep(50);
        late = tracker.GetHistoryIfChanged(lateChartId, -1);
    }
    Check.True(late.Values is { Length: 3 },
        "A chart opened later should be backfilled with the existing bounded history.");
    Check.Equal(1001f, late.Values![0].Value, "Late chart history should retain the oldest in-capacity sample.");
    Check.Equal(1003f, late.Values[2].Value, "Late chart history should include the newest sample.");

    // A reset while a backfill is in flight must win: restoring pre-reset samples would
    // contradict ResetPeaks having cleared the chart.
    const string racedId = "/cpu/package/voltage/0";
    for (int i = 0; i < 4; i++)
        tracker.Accept(new[] { Sample(racedId, 1f + i, firstTime.AddSeconds(20 + i)) });
    tracker.GetHistoryIfChanged(racedId, -1);   // arms the ring and schedules the backfill
    tracker.ResetPeaks();                        // invalidates it
    Thread.Sleep(300);
    HistoryReadResult raced = tracker.GetHistoryIfChanged(racedId, -1);
    Check.True(raced.Values is { Length: 0 },
        "A backfill scheduled before a peak reset must not repopulate cleared chart history.");
}

static void ConfigSanitizationTests()
{
    var config = new AppConfig
    {
        PollIntervalMs = 3100,
        ChartMinutes = 4,
        StartupDelaySeconds = 999,
        BatteryPollMultiplier = 0,
        EcReadEveryNTicks = 99,
        ThrottleSustainSeconds = -1,
        Language = "invalid",
        ThemeMode = "neon",
        HotkeyModifiers = 0,
        HotkeyKey = 999,
        AutoResetTime = "bad",
        ChartSensorIds = new List<string> { "cpu", "", "cpu", "gpu" },
        Profiles = new List<Profile>
        {
            new Profile
            {
                Name = "Default",
                TrayIcons = new List<TrayIconConfig>
                {
                    new() { RotateIntervalSec = 0, SensorIds = new List<string> { "cpu", "cpu", "" } },
                },
            },
        },
    };

    ConfigStore.Sanitize(config);

    Check.Equal(2000, config.PollIntervalMs, "Poll interval should choose the nearest supported value.");
    Check.Equal(3, config.ChartMinutes, "A four-minute legacy value should normalize to 3 minutes.");
    Check.Equal(60, config.StartupDelaySeconds, "Startup delay should be clamped.");
    Check.Equal(1, config.BatteryPollMultiplier, "Battery multiplier should be clamped.");
    Check.Equal(10, config.EcReadEveryNTicks, "EC cadence should be clamped.");
    Check.Equal(0, config.ThrottleSustainSeconds, "Throttle sustain should be clamped.");
    Check.Equal("auto", config.Language, "Unknown language should fall back to auto.");
    Check.Equal("auto", config.ThemeMode, "Unknown theme should fall back to auto.");
    Check.Equal(3, config.HotkeyModifiers, "Missing hotkey modifiers should restore Ctrl+Alt.");
    Check.Equal(0x4D, config.HotkeyKey, "Invalid hotkey should restore M.");
    Check.Equal("00:00", config.AutoResetTime, "Invalid auto-reset time should be repaired.");
    Check.Equal(2, config.ChartSensorIds.Count, "Chart ids should be nonempty and unique.");
    Check.Equal(1, config.Profiles[0].TrayIcons[0].RotateIntervalSec, "Tray rotation should have a minimum of one second.");
    Check.Equal(1, config.Profiles[0].TrayIcons[0].SensorIds.Count, "Tray sensor ids should be nonempty and unique.");
}

static void SuppressedStorageReferenceTests()
{
    const string removed = "/nvme/0/warning-temperature";
    const string kept = "/nvme/0/temperature";
    var profile = new Profile
    {
        Name = "Default",
        TrayIcons = new List<TrayIconConfig>
        {
            new() { SensorIds = new List<string> { removed } },
            new() { SensorIds = new List<string> { kept, removed } },
            new(), // automatic icon remains automatic
        },
        ThresholdOverrides = new Dictionary<string, Thresholds?>
        {
            [removed] = new Thresholds(),
        },
    };
    var config = new AppConfig
    {
        ActiveProfile = "Default",
        ChartSensorIds = new List<string> { removed, kept },
        SensorOverrides = new Dictionary<string, SensorOverride>
        {
            [removed] = new SensorOverride { Hidden = true },
        },
        Profiles = new List<Profile> { profile },
    };

    bool changed = ConfigStore.PruneSuppressedStorageTemperatureLimitReferences(config, new[] { removed });
    Check.True(changed, "Known suppressed SMART limit references should be removed.");
    Check.Sequence(new[] { kept }, config.ChartSensorIds, "Chart should retain live storage temperature only.");
    Check.True(!config.SensorOverrides.ContainsKey(removed), "Global override should be removed.");
    Check.True(!profile.ThresholdOverrides.ContainsKey(removed), "Profile threshold override should be removed.");
    Check.Equal(2, profile.TrayIcons.Count, "Only an explicitly legacy-only tray icon should be deleted.");
    Check.Sequence(new[] { kept }, profile.TrayIcons[0].SensorIds, "Carousel should retain its live member.");
    Check.Equal(0, profile.TrayIcons[1].SensorIds.Count, "Automatic tray icon should remain automatic.");
    Check.True(!ConfigStore.PruneSuppressedStorageTemperatureLimitReferences(config, new[] { removed }),
        "Pruning an already clean config should be a no-op.");
}

static void ProfileAlertTests()
{
    const string id = "/cpu/package/temperature/0";
    var defaultProfile = Profile.CreateDefault("Default");
    var gaming = new Profile
    {
        Name = "Gaming",
        ThresholdOverrides = new Dictionary<string, Thresholds?>
        {
            [id] = new Thresholds { Yellow = 5, Red = 10, SustainSeconds = 0, AlertEnabled = true },
        },
    };
    AppConfig current = new()
    {
        ActiveProfile = "Gaming",
        Profiles = new List<Profile> { defaultProfile, gaming },
    };
    var descriptor = new SensorDescriptor
    {
        Id = id,
        HardwareName = "CPU",
        Name = "Package",
        Category = SensorCategory.Cpu,
        Quantity = SensorQuantity.Temperature,
    };
    var alerts = new List<AlertEvent>();
    var engine = new AlertEngine(() => current);
    engine.AlertRaised += alerts.Add;

    engine.Accept(new[] { Sample(id, 20f, DateTime.UtcNow) }, new[] { descriptor });
    Check.Equal(1, alerts.Count, "Active-profile threshold override should enable its alert.");

    current = new AppConfig
    {
        ActiveProfile = "Default",
        Profiles = new List<Profile> { Profile.CreateDefault("Default") },
    };
    engine.ReloadConfig();
    engine.Accept(new[] { Sample(id, 20f, DateTime.UtcNow.AddSeconds(1)) }, new[] { descriptor });
    Check.Equal(1, alerts.Count, "Replacing config should make AlertEngine use the new active profile.");
}

static void CsvExportTests()
{
    string path = Path.Combine(Path.GetTempPath(), "WinMonitor-regression-" + Guid.NewGuid().ToString("N") + ".csv");
    try
    {
        var descriptor = new SensorDescriptor
        {
            Id = "/sensor/one",
            HardwareName = "Desk, \"A\"",
            Name = "Temperature",
            DisplayName = "Core, \"One\"",
            Category = SensorCategory.Cpu,
            Quantity = SensorQuantity.Temperature,
        };
        var throttle = new SensorDescriptor
        {
            Id = WellKnown.ThrottleSensorId,
            HardwareName = "CPU",
            Name = "Throttle",
            DisplayName = "CPU throttle",
            Category = SensorCategory.Cpu,
            Quantity = SensorQuantity.Level,
        };
        DateTime first = new(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc);
        using var tracker = new StatsTracker(historyCapacity: 1);
        tracker.Accept(new[]
        {
            Sample(descriptor.Id, 1.5f, first),
            Sample(throttle.Id, 0f, first),
        });
        tracker.Accept(new[]
        {
            Sample(descriptor.Id, 3.5f, first.AddSeconds(1)),
            Sample(throttle.Id, 1f, first.AddSeconds(1)),
        });

        tracker.ExportTimeSeriesCsv(path, new[] { descriptor, throttle });
        string csv = File.ReadAllText(path);
        Check.True(csv.Contains(
            "\"Desk, \"\"A\"\" / Core, \"\"One\"\" [Temperature]\"",
            StringComparison.Ordinal), "CSV should quote the combined sensor column safely.");
        Check.True(csv.Contains("False", StringComparison.Ordinal), "Boolean history should export False, not zero.");
        Check.True(csv.Contains("True", StringComparison.Ordinal), "Boolean history should export True, not one.");
        Check.True(csv.Contains("1.5", StringComparison.Ordinal), "CSV should use invariant numeric values.");
        Check.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
            "Two timestamps should produce two data rows plus the header.");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

static void HistoryLoggerDisableTests()
{
    var config = new AppConfig { Logging = new LoggingConfig { Enabled = false } };
    using var logger = new HistoryLogger(config, () => Array.Empty<SensorDescriptor>());
    FieldInfo? writerField = typeof(HistoryLogger).GetField(
        "_writer", BindingFlags.Instance | BindingFlags.NonPublic);
    Check.True(writerField is not null, "History logger writer field should exist.");

    var stream = new MemoryStream();
    var writer = new StreamWriter(stream);
    writerField!.SetValue(logger, writer);
    logger.Accept(Array.Empty<SensorSnapshot>(), complete: true);

    Check.True(writerField.GetValue(logger) is null,
        "Disabling logging should immediately release the open writer.");
    Check.True(!stream.CanWrite, "Disabling logging should close the underlying file stream.");
}

/// <summary>
/// A due CSV row must never be written from a snapshot that smart polling narrowed: the columns it
/// skipped would read as missing data. Regression for the sweep timer that drifted ahead of the
/// writer until the full sweep was consumed by an earlier tick.
/// </summary>
static void LoggingCompletenessTests()
{
    string directory = Path.Combine(Path.GetTempPath(), "WinMonitor-logtest-" + Guid.NewGuid().ToString("N"));
    var config = new AppConfig
    {
        Logging = new LoggingConfig { Enabled = true, IntervalSeconds = 1, RetentionDays = 0 },
    };
    var active = Descriptor("/test/active", "Active");
    var idle = Descriptor("/test/idle", "Idle");
    var descriptors = new[] { active, idle };

    try
    {
        DateTime now = DateTime.UtcNow;
        int sweepRequests = 0;
        void RequestSweep() => sweepRequests++;

        // Disposing closes and flushes the buffered writer, so the file is only read afterwards.
        using (var logger = new HistoryLogger(config, () => descriptors, directory))
        {
            // A partial tick: only the active sensor was sampled. A row is due (none written yet),
            // so the logger must pull a sweep instead of recording a blank column for the other.
            logger.Accept(new[] { Sample(active.Id, 40f, now) }, complete: false, RequestSweep);
            Check.Equal(1, sweepRequests, "A due row on a partial snapshot should request a full sweep.");
            Check.True(!Directory.Exists(directory) || Directory.GetFiles(directory, "*.csv").Length == 0,
                "A partial snapshot must not produce a CSV row.");

            // Still partial: the request must repeat, so a lost or mistimed sweep cannot stall the log.
            logger.Accept(new[] { Sample(active.Id, 41f, now.AddMilliseconds(200)) }, complete: false, RequestSweep);
            Check.Equal(2, sweepRequests, "Each due tick without a complete snapshot should re-request.");

            // The complete snapshot arrives: the row is written now, with both columns populated.
            logger.Accept(new[]
            {
                Sample(active.Id, 42f, now.AddMilliseconds(400)),
                Sample(idle.Id, 55f, now.AddMilliseconds(400)),
            }, complete: true, RequestSweep);

            // Inside the interval: a further complete snapshot must not add a second row.
            logger.Accept(new[]
            {
                Sample(active.Id, 43f, now.AddMilliseconds(600)),
                Sample(idle.Id, 56f, now.AddMilliseconds(600)),
            }, complete: true, RequestSweep);
            Check.Equal(2, sweepRequests, "A snapshot inside the logging interval should not re-request.");
        }

        string[] files = Directory.GetFiles(directory, "winmonitor-*.csv");
        Check.Equal(1, files.Length, "A complete snapshot should open exactly one CSV.");
        string[] lines = File.ReadAllLines(files[0]);
        Check.Equal(2, lines.Length, "The CSV should hold a header plus exactly one data row.");
        string[] fields = lines[1].Split(',');
        Check.Equal(3, fields.Length, "The row should carry a timestamp plus both sensor columns.");
        Check.True(fields[1].Length > 0 && fields[2].Length > 0,
            "Neither column may be blank in a row written from a complete snapshot.");
    }
    finally
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
    }
}

/// <summary>
/// Shutdown must distinguish "asked the poll thread to stop" from "the poll thread has left".
/// A tick wedged in a native LHM/EC call still owns the Computer and the wait handles, so a
/// timed-out join may not be treated as an exit.
/// </summary>
static void PollThreadLifetimeTests()
{
    using var release = new ManualResetEventSlim(false);
    using var entered = new ManualResetEventSlim(false);
    var handle = new PollThreadHandle();

    Check.True(handle.Join(0), "An unused handle has nothing to wait for.");

    Check.True(handle.Start(() => { entered.Set(); release.Wait(); }, "test-worker", ThreadPriority.Normal),
        "Starting the first worker should succeed.");
    Check.True(entered.Wait(TimeSpan.FromSeconds(10)), "The worker should reach its body.");
    Check.True(handle.IsRunning, "A worker inside its body should report as running.");

    // The worker is deliberately stuck: the join must fail and the handle must keep the reference.
    Check.True(!handle.Join(50), "A join that times out must not report an exit.");
    Check.True(handle.Pending is not null, "A worker that was never seen leaving must stay pending.");

    bool secondStarted = handle.Start(() => { }, "second-worker", ThreadPriority.Normal);
    Check.True(!secondStarted, "A second worker must not start on top of a live one.");

    release.Set();
    Check.True(handle.Join(10000), "A worker that exits should be observed.");
    Check.True(handle.Pending is null, "An observed exit should release the worker reference.");

    using var restarted = new ManualResetEventSlim(false);
    Check.True(handle.Start(() => restarted.Set(), "third-worker", ThreadPriority.Normal),
        "A new worker should start once the previous exit was observed.");
    Check.True(restarted.Wait(TimeSpan.FromSeconds(10)), "The replacement worker should run.");
    Check.True(handle.Join(10000), "The replacement worker should be joinable.");
}

/// <summary>
/// The tray's latest-value cache is keyed by sensor id and fed by the poll thread; a descriptor
/// rebuild must drop ids that no longer exist or the cache grows for the whole session.
/// </summary>
static void TrayLatestValuePruneTests()
{
    MethodInfo? prune = typeof(TrayIconManager).GetMethod(
        "PruneStaleValues", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(prune is not null, "Tray value pruning should exist.");

    var latest = new System.Collections.Concurrent.ConcurrentDictionary<string, float?>(StringComparer.Ordinal);
    for (int i = 0; i < 500; i++) latest["/test/sensor/" + i] = i;
    latest["/test/null"] = null;

    var current = new[] { Descriptor("/test/sensor/7", "Kept"), Descriptor("/test/sensor/11", "Also kept") };
    prune!.Invoke(null, new object[] { latest, current });

    Check.Equal(2, latest.Count, "Pruning should keep only values for current descriptors.");
    Check.True(latest.ContainsKey("/test/sensor/7") && latest.ContainsKey("/test/sensor/11"),
        "Pruning must not drop values that are still described.");
    Check.True(!latest.ContainsKey("/test/null"),
        "A cached null for a removed descriptor should be pruned like any other entry.");

    // An empty descriptor list is a real state (a rescan that found nothing) and must clear, not throw.
    prune.Invoke(null, new object[] { latest, Array.Empty<SensorDescriptor>() });
    Check.Equal(0, latest.Count, "A rebuild with no descriptors should empty the cache.");

    // Correct pruning that nothing calls is still an unbounded cache: drive the real rebuild path.
    var config = new AppConfig();
    config.Active.TrayIcons.Clear();          // no NotifyIcon needed to exercise the cache
    using var stats = new StatsTracker(historyCapacity: 1);
    using var tray = new TrayIconManager(config, stats, new ImmediateSync());
    DateTime now = DateTime.UtcNow;
    tray.Accept(new[] { Sample("/test/gone", 1f, now), Sample("/test/kept", 2f, now) });
    tray.Rebuild(new[] { Descriptor("/test/kept", "Kept") });

    FieldInfo? latestField = typeof(TrayIconManager).GetField(
        "_latest", BindingFlags.Instance | BindingFlags.NonPublic);
    Check.True(latestField is not null, "Tray manager should hold its latest-value cache in _latest.");
    var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, float?>)latestField!.GetValue(tray)!;
    Check.True(!cache.ContainsKey("/test/gone"),
        "A descriptor rebuild should prune values for sensors that no longer exist.");
    Check.True(cache.ContainsKey("/test/kept"),
        "A descriptor rebuild must keep values that are still described.");
}

/// <summary>
/// A native call that never returns cannot be cancelled, so the gate's contract is that the
/// *caller* stops waiting and nothing the abandoned call owns is ever reclaimed. Uses a blocking
/// delegate rather than hardware, so the wedge is deterministic.
/// </summary>
static void NativeCallGateTests()
{
    using var gate = new NativeCallGate("TestGate");
    int completed = 0;

    Check.True(gate.TryRun(() => completed++, 5000), "A prompt call should complete.");
    Check.Equal(1, completed, "The delegate should have run exactly once.");
    Check.True(!gate.Wedged, "A completed call must not close the gate.");

    // An exception inside the delegate is the caller's business, not a gate failure.
    Check.True(gate.TryRun(() => throw new InvalidOperationException("boom"), 5000),
        "A delegate that throws still counts as returned.");
    Check.True(!gate.Wedged, "A throwing delegate must not close the gate.");

    using var release = new ManualResetEventSlim(false);
    using var entered = new ManualResetEventSlim(false);
    try
    {
        Check.True(!gate.TryRun(() => { entered.Set(); release.Wait(); }, 100),
            "A call that outlives its timeout must report failure.");
        Check.True(entered.Wait(TimeSpan.FromSeconds(10)), "The wedged delegate should have started.");
        Check.True(gate.Wedged, "A timed-out call must close the gate.");

        // Fail fast afterwards: queueing behind a thread that will never return is the failure
        // mode the gate exists to prevent.
        int afterWedge = 0;
        Check.True(!gate.TryRun(() => afterWedge++, 5000), "A closed gate must accept no further work.");
        Check.Equal(0, afterWedge, "A closed gate must not run the delegate.");
    }
    finally
    {
        release.Set();
    }
}

/// <summary>
/// The shared "Global\Access_EC" mutex is what keeps EC port access from interleaving with the
/// ACPI driver. A session-local "Access_EC" is a different kernel object, so falling back to it
/// would synchronize against nothing — the EC must stay unavailable instead.
/// </summary>
static void EcMutexFailClosedTests()
{
    Type type = typeof(EmbeddedController);
    Check.True(type.GetMethod("TryOpenEcMutex", BindingFlags.Instance | BindingFlags.NonPublic) is null,
        "The fallback mutex opener should be gone, not merely unused.");

    MethodInfo? open = type.GetMethod("TryOpenSharedMutex", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(open is not null, "A shared-mutex opener should exist.");
    Check.True(open!.ReturnType == typeof(Mutex), "The opener should report failure by returning null.");

    FieldInfo? nameField = type.GetField("SharedMutexName", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(nameField is not null, "The shared mutex name should be a single named constant.");
    Check.Equal("Global\\Access_EC", (string)nameField!.GetRawConstantValue()!,
        "EC access must use the Global namespace every other tool uses.");

    // No source path may perform EC work without the mutex: the opener is the only producer, and
    // Initialize is the only consumer, so an unavailable mutex has to leave the EC off.
    string source = ReadRepositoryFile("src/WinMonitor/Core/EmbeddedController.cs");
    Check.True(!source.Contains("new Mutex(false, \"Access_EC\")", StringComparison.Ordinal),
        "A session-local Access_EC fallback must not be reintroduced.");
    Check.True(source.Contains("ec_mutex_unavailable", StringComparison.Ordinal),
        "Failing to obtain the shared mutex should surface a reason.");
    Check.True(!source.Contains("_ecMutex is not null && !held", StringComparison.Ordinal),
        "Reads must not treat a missing mutex as permission to proceed unsynchronized.");

    // A real acquisition still has to work, and the budget must bound the wait rather than
    // spending a fixed 200 ms on a contended mutex.
    MethodInfo? acquire = type.GetMethod("AcquireEcMutex", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(acquire is not null, "The mutex acquisition helper should exist.");
    ParameterInfo[] parameters = acquire!.GetParameters();
    Check.Equal(2, parameters.Length, "Acquisition should take the mutex to use and the remaining budget.");
    // The mutex is passed in, not read from the field: a Reset during a wedged call clears the
    // field, and the batch still has to release the exact object it acquired.
    Check.True(parameters[0].ParameterType == typeof(Mutex), "The batch's own mutex should be passed in.");
    Check.True(parameters[1].ParameterType == typeof(int), "The remaining budget should be milliseconds.");

    using var controller = new EmbeddedController();
    Check.True(!controller.Available, "A controller with no driver must not report itself available.");
    controller.ReadRegisters(new[] { 0xB0 }, out bool[] ok);
    Check.True(ok.Length == 1 && !ok[0], "An unavailable controller must report every register as unread.");
}

/// <summary>
/// Every HICON that <see cref="IconRenderer"/> hands out has to reach exactly one DestroyIcon.
/// The interesting cases are the failure paths, where nothing else can still own the handle.
/// </summary>
static void IconHandleOwnershipTests()
{
    MethodInfo? release = typeof(IconRenderer).GetMethod(
        "ReleaseHandle", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(release is not null,
        "A raw-handle release path should exist for HICONs that never reach a wrapper.");

    // A zero handle is what a failed GetHicon yields; releasing it must be a no-op, not a throw.
    release!.Invoke(null, new object[] { IntPtr.Zero });

    // Round-trip a real icon: rendering then releasing must not throw, and the wrapper must be
    // disposed by the release (its handle is destroyed, so touching it afterwards is invalid).
    Icon icon = IconRenderer.RenderText("42", Color.White, Color.Transparent, bold: false);
    Check.True(icon.Handle != IntPtr.Zero, "A rendered icon should carry a live handle.");
    IconRenderer.ReleaseIcon(icon);

    string renderer = ReadRepositoryFile("src/WinMonitor/Tray/IconRenderer.cs");
    Check.True(renderer.Contains("ReleaseHandle(hIcon)", StringComparison.Ordinal),
        "The GetHicon/FromHandle window must destroy the handle if wrapping fails.");

    string tray = ReadRepositoryFile("src/WinMonitor/Tray/TrayIconManager.cs");
    Check.True(tray.Contains("if (next is not null) IconRenderer.ReleaseIcon(next);", StringComparison.Ordinal),
        "An icon the shell refused must be released, not dropped.");
    Check.True(tray.Contains("try { slot.Icon.Dispose(); } catch (Exception) { }", StringComparison.Ordinal),
        "A NotifyIcon that throws on dispose must not skip the HICON release that follows it.");
}

/// <summary>
/// A config that could not be read AND could not be moved aside is still on disk. Saving defaults
/// over it would destroy the file we failed to read — which, for a locked or briefly denied file,
/// is a config the user still wants.
/// </summary>
static void ConfigBackupFailureTests()
{
    // Backup reports failure rather than swallowing it.
    MethodInfo? backup = typeof(ConfigStore).GetMethod(
        "TryBackupCorrupt", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(backup is not null, "The corrupt-config backup helper should exist.");
    Check.True(backup!.ReturnType == typeof(bool), "Backup must report whether it succeeded.");

    string missing = Path.Combine(Path.GetTempPath(), "WinMonitor-absent-" + Guid.NewGuid().ToString("N"));
    Check.True((bool)backup.Invoke(null, new object[] { missing })!,
        "A file that is already gone needs no protection.");

    // The whole chain: unreadable content, an unmovable file, then a save.
    using (var scope = new ScopedConfigDirectory())
    {
        string path = Path.Combine(scope.Path, "config.json");
        const string corrupt = "{ this is not json";
        File.WriteAllText(path, corrupt);

        // Shared for reading but not for delete: Load can read it, File.Move cannot move it.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AppConfig loaded = ConfigStore.Load();
            Check.True(loaded is not null, "A corrupt config should still yield defaults.");
            Check.True(!File.Exists(path + ".bak"), "The backup was expected to fail in this test.");
        }

        ConfigStore.Save(new AppConfig());
        Check.Equal(corrupt, File.ReadAllText(path),
            "A config that could not be backed up must not be overwritten by a later save.");
        Check.True(File.Exists(path + ".recovered"),
            "The save should divert to the recovery sibling instead.");
    }

    // Control: when the backup succeeds, saving to config.json is correct — the original is safe
    // under .bak and refusing to write would leave the user with no config at all.
    using (var scope = new ScopedConfigDirectory())
    {
        string path = Path.Combine(scope.Path, "config.json");
        File.WriteAllText(path, "{ this is not json");
        ConfigStore.Load();
        Check.True(File.Exists(path + ".bak"), "A movable corrupt config should be backed up.");
        ConfigStore.Save(new AppConfig());
        Check.True(!File.Exists(path + ".recovered"),
            "A successful backup should not divert the save.");
        Check.True(File.ReadAllText(path).Contains("SchemaVersion", StringComparison.Ordinal),
            "The save should write a real config once the original is preserved.");
    }
}

/// <summary>
/// Every on-disk config walks the migration chain from wherever it was written. Real users' files
/// start at v1, v2 or v3; a file from a newer build must be read best-effort and never overwritten.
/// </summary>
static void MigrationChainTests()
{
    // v3 -> v4 turns tray units on, so ShowUnit is the observable that proves each step ran.
    string ConfigAtVersion(int version) =>
        "{\"SchemaVersion\":" + version + ",\"Profiles\":[{\"Name\":\"Default\",\"TrayIcons\":"
        + "[{\"SensorIds\":[\"/cpu/0/temperature/0\"],\"ShowUnit\":false}]}]}";

    foreach (int start in new[] { 1, 2, 3 })
    {
        using var scope = new ScopedConfigDirectory();
        File.WriteAllText(Path.Combine(scope.Path, "config.json"), ConfigAtVersion(start));
        AppConfig config = ConfigStore.Load();
        Check.Equal(4, config.SchemaVersion, $"A v{start} config should migrate to the current schema.");
        Check.True(config.Profiles[0].TrayIcons[0].ShowUnit,
            $"The v3 to v4 step must run for a config that started at v{start}.");
        Check.True(!ConfigStore.IsLoadedFromNewerSchema, $"A v{start} config is not from a newer build.");
    }

    // A readable future schema: usable this session, but its unknown fields must survive.
    using (var scope = new ScopedConfigDirectory())
    {
        string path = Path.Combine(scope.Path, "config.json");
        const string future = "{\"SchemaVersion\":99,\"PollIntervalMs\":2000,\"SomethingNew\":42}";
        File.WriteAllText(path, future);
        ConfigStore.Load();
        Check.True(ConfigStore.IsLoadedFromNewerSchema, "A v99 config should be recognised as newer.");
        ConfigStore.Save(new AppConfig());
        Check.Equal(future, File.ReadAllText(path), "A newer-schema config must never be overwritten.");
        Check.True(File.Exists(path + ".newer-version"), "Saves should divert to the newer-version sibling.");
    }

    // A future schema this build cannot even parse: it is not evidence of corruption, so the
    // source stays untouched and saves divert rather than backing it up as broken.
    using (var scope = new ScopedConfigDirectory())
    {
        string path = Path.Combine(scope.Path, "config.json");
        const string unparseable = "{\"SchemaVersion\":99,\"Profiles\":\"now a string\"}";
        File.WriteAllText(path, unparseable);
        ConfigStore.Load();
        ConfigStore.Save(new AppConfig());
        Check.Equal(unparseable, File.ReadAllText(path),
            "An unparseable newer-schema config must be preserved verbatim.");
        Check.True(!File.Exists(path + ".bak"), "A newer-schema file must not be treated as corrupt.");
    }
}

/// <summary>
/// The layout fingerprint decides whether today's CSV keeps its columns. It must cover everything
/// that changes what a column MEANS, not merely what it is called.
/// </summary>
static void CsvLayoutIdentityTests()
{
    MethodInfo? fingerprint = typeof(HistoryLogger).GetMethod(
        "BuildLayoutFingerprint", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(fingerprint is not null, "The layout fingerprint builder should exist.");

    string Fingerprint(SensorDescriptor d) =>
        (string)fingerprint!.Invoke(null, new object[] { new[] { d } })!;

    // An EC sensor id is "/ec/reg/XX/{Kind}" and carries no quantity, so switching one from Fan to
    // Temperature in the EC Explorer leaves id, name and header text identical.
    var asFan = new SensorDescriptor
    {
        Id = "/ec/reg/B0/RawByte", HardwareName = "Embedded Controller", Name = "EC B0",
        DisplayName = "EC B0", Category = SensorCategory.Fan, Quantity = SensorQuantity.Fan,
    };
    var asTemperature = new SensorDescriptor
    {
        Id = asFan.Id, HardwareName = asFan.HardwareName, Name = asFan.Name,
        DisplayName = asFan.DisplayName, Category = SensorCategory.Motherboard,
        Quantity = SensorQuantity.Temperature,
    };
    Check.True(Fingerprint(asFan) != Fingerprint(asTemperature),
        "Changing a column from RPM to Celsius must roll to a new file, not reuse the column.");

    var movedHardware = new SensorDescriptor
    {
        Id = asFan.Id, HardwareName = "Other controller", Name = asFan.Name,
        DisplayName = asFan.DisplayName, Category = asFan.Category, Quantity = asFan.Quantity,
    };
    Check.True(Fingerprint(asFan) != Fingerprint(movedHardware),
        "The owning hardware is part of a column's identity.");

    // ...and an identical descriptor still matches, or every restart would start a new file.
    var same = new SensorDescriptor
    {
        Id = asFan.Id, HardwareName = asFan.HardwareName, Name = asFan.Name,
        DisplayName = asFan.DisplayName, Category = asFan.Category, Quantity = asFan.Quantity,
    };
    Check.Equal(Fingerprint(asFan), Fingerprint(same), "An unchanged layout must keep its file.");
}

/// <summary>
/// A crash can leave the day's CSV ending mid-row. Appending there glues the next complete row onto
/// the fragment and loses both.
/// </summary>
static void CsvTornFileTests()
{
    string directory = Path.Combine(Path.GetTempPath(), "WinMonitor-torn-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var descriptor = Descriptor("/test/torn", "Torn");
    var descriptors = new[] { descriptor };
    var config = new AppConfig
    {
        Logging = new LoggingConfig { Enabled = true, IntervalSeconds = 5, RetentionDays = 0 },
    };

    try
    {
        // Produce a genuine file plus sidecar, then tear the last row off mid-write.
        string first;
        using (var logger = new HistoryLogger(config, () => descriptors, directory))
        {
            logger.Accept(new[] { Sample(descriptor.Id, 40f, DateTime.UtcNow) }, complete: true);
            first = Directory.GetFiles(directory, "winmonitor-*.csv")[0];
        }
        File.AppendAllText(first, "2026-08-14T10:00:00,4");   // no newline: a torn row
        string tornContent = File.ReadAllText(first);

        using (var logger = new HistoryLogger(config, () => descriptors, directory))
        {
            logger.Accept(new[] { Sample(descriptor.Id, 41f, DateTime.UtcNow) }, complete: true);
        }

        Check.Equal(tornContent, File.ReadAllText(first),
            "A torn CSV must be left exactly as it is, not appended to or repaired.");
        string[] files = Directory.GetFiles(directory, "winmonitor-*.csv");
        Check.Equal(2, files.Length, "Logging should continue in the next suffixed file.");
        string continuation = files.First(f => !string.Equals(f, first, StringComparison.Ordinal));
        Check.Equal(2, File.ReadAllLines(continuation).Length,
            "The continuation file should hold its own header plus the new row.");
    }
    finally
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
    }
}

/// <summary>
/// A spool write failure must cost only the records after it. Reporting nothing throws away a whole
/// session to punish its last few seconds.
/// </summary>
static void SessionSpoolFaultTests()
{
    string path = Path.Combine(Path.GetTempPath(), "WinMonitor-spool-" + Guid.NewGuid().ToString("N") + ".csv");
    var descriptor = Descriptor("/test/spool", "Spooled");
    var descriptors = new[] { descriptor };
    DateTime t0 = new(2026, 8, 14, 9, 0, 0, DateTimeKind.Utc);

    using var tracker = new StatsTracker(historyCapacity: 4);
    tracker.Accept(new[] { Sample(descriptor.Id, 10f, t0) });
    tracker.Accept(new[] { Sample(descriptor.Id, 11f, t0.AddSeconds(1)) });

    // Same timestamp twice: the exporter groups by tick and the last value wins. Making that
    // explicit keeps the spool's implicit one-row-per-tick contract from drifting.
    tracker.Accept(new[] { Sample(descriptor.Id, 12f, t0.AddSeconds(2)) });
    tracker.Accept(new[] { Sample(descriptor.Id, 99f, t0.AddSeconds(2)) });

    // Simulate exactly what Append's catch does on a write failure.
    object spool = PrivateField(tracker, "_sessionHistory")!;
    spool.GetType().GetMethod("CloseWriter", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(spool, null);
    spool.GetType().GetField("_faulted", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(spool, true);

    tracker.Accept(new[] { Sample(descriptor.Id, 50f, t0.AddSeconds(3)) });   // dropped

    try
    {
        tracker.ExportTimeSeriesCsv(path, descriptors);
        string[] lines = File.ReadAllLines(path);
        Check.Equal(4, lines.Length,
            "A faulted spool must still export the records written before the failure.");
        Check.True(lines[1].Contains("10", StringComparison.Ordinal), "The first retained row should survive.");
        Check.True(lines[3].Contains("99", StringComparison.Ordinal),
            "Records sharing a timestamp collapse to one row, last value wins.");
        Check.True(!lines[3].Contains("12", StringComparison.Ordinal),
            "The superseded value for that timestamp must not appear.");

        // With the spool itself gone there is nothing trustworthy to export, and a header-only
        // file would be indistinguishable from an empty session.
        string spoolPath = (string)PrivateField(spool, "_path")!;
        File.Delete(spoolPath);
        bool threw = false;
        try { tracker.ExportTimeSeriesCsv(path, descriptors); }
        catch (IOException) { threw = true; }
        Check.True(threw, "An unreadable history must fail the export rather than write a header.");
    }
    finally
    {
        try { File.Delete(path); } catch { }
    }
}

/// <summary>
/// Export opened the destination with append:false, truncating the user's previous file the instant
/// the dialog was confirmed — so a failure part-way through left them with neither file.
/// </summary>
static void CsvExportAtomicityTests()
{
    string directory = Path.Combine(Path.GetTempPath(), "WinMonitor-export-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "export.csv");
    var descriptor = Descriptor("/test/export", "Exported");
    var descriptors = new[] { descriptor };

    try
    {
        const string sentinel = "PREVIOUS EXPORT";
        File.WriteAllText(path, sentinel);

        using (var tracker = new StatsTracker(historyCapacity: 2))
        {
            // Destroy the spool so the export must fail after the destination already exists.
            object spool = PrivateField(tracker, "_sessionHistory")!;
            spool.GetType().GetMethod("CloseWriter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(spool, null);
            File.Delete((string)PrivateField(spool, "_path")!);

            bool threw = false;
            try { tracker.ExportTimeSeriesCsv(path, descriptors); }
            catch (IOException) { threw = true; }
            Check.True(threw, "A failed export should report the failure.");
            Check.Equal(sentinel, File.ReadAllText(path),
                "A failed export must leave the previous file untouched.");
        }
        Check.Equal(0, Directory.GetFiles(directory, "*.tmp-*").Length,
            "A failed export must not leave a temporary file behind.");

        using (var tracker = new StatsTracker(historyCapacity: 2))
        {
            tracker.Accept(new[] { Sample(descriptor.Id, 7f, DateTime.UtcNow) });
            tracker.ExportTimeSeriesCsv(path, descriptors);
        }
        Check.True(File.ReadAllText(path) != sentinel, "A successful export should replace the file.");
        Check.Equal(0, Directory.GetFiles(directory, "*.tmp-*").Length,
            "A successful export must not leave a temporary file behind.");
    }
    finally
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
    }
}

/// <summary>
/// Tray icons are the one collection edited from two places at once: the Settings dialog and the
/// main window's row context menu, which adds or removes a single-sensor icon on the LIVE config.
/// Preferring the draft wholesale made an icon toggled on in the main list vanish on Apply.
/// </summary>
static void TrayIconMergeTests()
{
    MethodInfo? merge = typeof(SettingsForm).GetMethod(
        "MergeDraftNode", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(merge is not null, "The settings three-way merge should exist.");

    static JsonArray Icons(params string[] specs)
    {
        var array = new JsonArray();
        foreach (string spec in specs)
        {
            string[] parts = spec.Split('@');
            array.Add(new JsonObject
            {
                ["SensorIds"] = new JsonArray(JsonValue.Create(parts[0])),
                ["RotateIntervalSec"] = int.Parse(parts.Length > 1 ? parts[1] : "5"),
            });
        }
        return array;
    }

    JsonArray Merge(JsonArray baseline, JsonArray draft, JsonArray live)
    {
        object? result = merge!.Invoke(null, new object?[] { baseline, draft, live, "TrayIcons" });
        Check.True(result is JsonArray, "Merging tray icon arrays should produce an array.");
        return (JsonArray)result!;
    }

    static List<string> Sensors(JsonArray array)
    {
        var ids = new List<string>();
        foreach (JsonNode? node in array)
            ids.Add(node?["SensorIds"]?[0]?.GetValue<string>() ?? "");
        return ids;
    }

    // The reported case: Settings edits icon A while the main window toggles B into the tray.
    JsonArray merged = Merge(Icons("A"), Icons("A@9"), Icons("A", "B"));
    Check.Sequence(new[] { "A", "B" }, Sensors(merged),
        "An icon added outside the dialog must survive Apply.");
    Check.Equal(9, merged[0]!["RotateIntervalSec"]!.GetValue<int>(),
        "The draft's own edit to A must be kept.");

    // The mirror case, reachable through the obsolete-sensor prune: an icon removed live while the
    // draft did not touch it stays removed rather than being resurrected.
    Check.Sequence(new[] { "A" }, Sensors(Merge(Icons("A", "B"), Icons("A@9", "B"), Icons("A"))),
        "An icon removed outside the dialog must not come back.");

    // A draft removal is explicit and must win over an untouched live copy.
    Check.Sequence(new[] { "A" }, Sensors(Merge(Icons("A", "B"), Icons("A"), Icons("A", "B"))),
        "An icon deleted in the dialog must stay deleted.");

    // Draft ordering is intentional and must not be reshuffled by the merge.
    Check.Sequence(new[] { "B", "A" }, Sensors(Merge(Icons("A", "B"), Icons("B", "A"), Icons("A", "B"))),
        "Reordering in the dialog must be preserved.");

    // Duplicate sensor sets make the content key ambiguous; falling back to the draft is defined
    // behaviour, not an accident.
    Check.Sequence(new[] { "A", "A" }, Sensors(Merge(Icons("A"), Icons("A", "A"), Icons("A", "B"))),
        "Ambiguous duplicate keys should fall back to the draft.");
}

/// <summary>
/// ExtractAssociatedIcon transfers ownership of a real HICON. Form.Dispose does not release an Icon
/// it was merely assigned, so every window that extracts one has to hold and free it.
/// </summary>
static void WindowIconOwnershipTests()
{
    // EcExplorerForm is constructible without a running application, so its disposal is checked
    // for real: build one, dispose it, and prove the icon it extracted was released.
    using var ec = new EmbeddedController();
    var form = new EcExplorerForm(ec, new EcConfig(), () => { }, () => (float.NaN, float.NaN));
    FieldInfo? field = typeof(EcExplorerForm).GetField("_windowIcon", BindingFlags.Instance | BindingFlags.NonPublic);
    Check.True(field is not null, "EcExplorerForm should retain the icon it extracts.");

    var icon = (Icon?)field!.GetValue(form);
    form.Dispose();
    if (icon is not null)
    {
        bool released = false;
        try { _ = icon.Handle; } catch (ObjectDisposedException) { released = true; }
        Check.True(released, "Disposing the form must release the icon it owns.");
    }
    Check.True(field.GetValue(form) is null, "Disposal should clear the owned icon reference.");

    // SettingsForm needs a live WinMonitorContext (which opens hardware), so its identical pattern
    // is only guarded structurally. This is a shape check, not proof that disposal releases.
    FieldInfo? settingsField = typeof(SettingsForm).GetField(
        "_windowIcon", BindingFlags.Instance | BindingFlags.NonPublic);
    Check.True(settingsField is not null, "SettingsForm should retain the icon it extracts.");
    Check.True(settingsField!.FieldType == typeof(Icon), "SettingsForm's window icon field should be an Icon.");
    MethodInfo? dispose = typeof(SettingsForm).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null, new[] { typeof(bool) }, modifiers: null);
    Check.True(dispose is not null && dispose.DeclaringType == typeof(SettingsForm),
        "SettingsForm should override Dispose(bool) to release it.");
}

/// <summary>
/// A timed-out EC batch is still running and still owns its buffers. Whatever it writes afterwards
/// must never reach the caller, whose arrays go straight into the sensor pipeline.
/// </summary>
static void EcTimeoutBufferOwnershipTests()
{
    MethodInfo? publish = typeof(EmbeddedController).GetMethod(
        "PublishBatch", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(publish is not null, "The batch publish step should exist.");

    var workerData = new byte[] { 0x11, 0x22 };
    var workerFlags = new[] { true, true };
    var callerData = new byte[2];
    var callerFlags = new bool[2];

    // Timed out: nothing is published, and a late write by the worker changes nothing.
    publish!.Invoke(null, new object[] { false, workerData, workerFlags, callerData, callerFlags });
    Check.True(!callerFlags[0] && !callerFlags[1], "A timed-out batch must report nothing as read.");
    workerData[0] = 0x99;
    Check.Equal((byte)0, callerData[0], "A late worker write must not reach the caller's buffer.");

    // Completed: results are copied, and the caller's arrays remain its own afterwards.
    publish.Invoke(null, new object[] { true, workerData, workerFlags, callerData, callerFlags });
    Check.Equal((byte)0x99, callerData[0], "A completed batch should publish its values.");
    Check.True(callerFlags[0] && callerFlags[1], "A completed batch should publish its flags.");
    workerData[1] = 0x77;
    Check.Equal((byte)0x22, callerData[1], "Publishing must copy, not alias, the worker's buffer.");

    // An unavailable controller must still leave the caller's arrays untouched and all-false.
    using var controller = new EmbeddedController();
    byte[] values = controller.ReadRegisters(new[] { 0xB0, 0xB1 }, out bool[] ok);
    Check.True(!ok[0] && !ok[1], "An unavailable EC reports every register as unread.");
    Check.True(values[0] == 0 && values[1] == 0, "An unavailable EC returns no values.");
}

/// <summary>
/// The tray canvas is the shell's small-icon size, which moves when the display scale changes. It
/// used to be fixed for the process lifetime, leaving icons rendered at the startup DPI.
/// </summary>
static void TrayCanvasDpiTests()
{
    int original = IconRenderer.CurrentCanvasSize;
    Check.True(original > 0, "The canvas should have a real size before any change.");

    try
    {
        // ReleaseIcon, never `using`: Icon.FromHandle does not own its HICON, so disposing the
        // wrapper alone leaks the handle — the very rule these checks exist to protect.
        IconRenderer.SetCanvasSizeProviderForTests(() => 24);
        Check.Equal(24, IconRenderer.CurrentCanvasSize, "A changed shell metric should be adopted.");
        Icon larger = IconRenderer.RenderText("42", Color.White, Color.Transparent, bold: false);
        Check.Equal(new Size(24, 24), larger.Size, "Icons should render at the new canvas size.");
        IconRenderer.ReleaseIcon(larger);
        Check.True(!IconRenderer.RefreshMetrics(), "An unchanged metric must not force a re-render.");

        IconRenderer.SetCanvasSizeProviderForTests(() => 16);
        Check.Equal(16, IconRenderer.CurrentCanvasSize, "A later change should be adopted too.");
        Icon smaller = IconRenderer.RenderText("42", Color.White, Color.Transparent, bold: false);
        Check.Equal(new Size(16, 16), smaller.Size,
            "Font and path caches sized against the old canvas must be rebuilt, not reused.");
        IconRenderer.ReleaseIcon(smaller);
    }
    finally
    {
        IconRenderer.SetCanvasSizeProviderForTests(null);
    }
    Check.Equal(original, IconRenderer.CurrentCanvasSize, "Clearing the test seam should restore the real size.");
}

static object? PrivateField(object instance, string name)
{
    FieldInfo? field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    Check.True(field is not null, $"Field {name} should exist on {instance.GetType().Name}.");
    return field!.GetValue(instance);
}

/// <summary>Reads a repository source file relative to the solution root.</summary>
static string ReadRepositoryFile(string relativePath)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relativePath)))
        dir = dir.Parent;
    Check.True(dir is not null, $"Should locate {relativePath} above the test output directory.");
    return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
}

static void IntelThermalStatusDecodeTests()
{
    Type? type = typeof(StatsTracker).Assembly.GetType("WinMonitor.Core.IntelThermalStatusReader");
    MethodInfo? decode = type?.GetMethod("DecodeStatus", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(decode is not null, "Intel thermal-status decoder should exist.");

    bool Decode(ulong value) => (bool)decode!.Invoke(null, new object[] { value })!;
    Check.True(!Decode(0), "An empty package status must be false.");
    Check.True(Decode(1UL << 0), "The current thermal-status bit must be true.");
    Check.True(Decode(1UL << 2), "The current PROCHOT-status bit must be true.");
    Check.True(!Decode((1UL << 1) | (1UL << 3)), "Sticky log bits must not report current throttling.");
}

static void TrayUnitFormattingTests()
{
    MethodInfo? format = typeof(TrayIconManager).GetMethod(
        "FormatShort", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(format is not null, "Tray compact formatter should exist.");

    string Format(SensorQuantity quantity, float value)
    {
        var descriptor = new SensorDescriptor
        {
            Id = "/test/" + quantity,
            HardwareName = "Test",
            Name = quantity.ToString(),
            Category = SensorCategory.Other,
            Quantity = quantity,
        };
        return (string)format!.Invoke(null, new object?[] { descriptor, (float?)value })!;
    }

    // The tray glyph is digits only. Every character spent on a unit is taken from the number,
    // and past three glyphs adjacent stems merge at 16 px. Magnitudes are folded into the value
    // rather than spelled out, so no scale information is lost; the tooltip states the unit.
    Check.Equal("34", Format(SensorQuantity.Fan, 3400f), "Fan RPM should render in hundreds.");
    Check.Equal("900", Format(SensorQuantity.Fan, 900f), "Sub-1000 rpm stays a raw value.");
    Check.Equal("4.2", Format(SensorQuantity.Frequency, 4200f), "Frequency should render as GHz.");
    Check.Equal("850", Format(SensorQuantity.Frequency, 850f), "Sub-GHz frequency stays in MHz.");
    Check.Equal("1.2", Format(SensorQuantity.Voltage, 1.2f), "Voltage drops its unit.");
    Check.Equal("2", Format(SensorQuantity.Data, 2048f), "Large data values render as TB.");
    Check.Equal("65", Format(SensorQuantity.Power, 65f), "Power drops its unit.");
    Check.Equal("42", Format(SensorQuantity.Load, 42f), "Load drops its unit.");
    Check.Equal("100", Format(SensorQuantity.Temperature, 100f), "Temperature drops its unit.");

    // Sweep each quantity across the range it can realistically report: no reading may exceed
    // three glyphs, the most a 16 px canvas renders without adjacent stems merging.
    var ranges = new Dictionary<SensorQuantity, float[]>
    {
        [SensorQuantity.Temperature] = new[] { 0f, 45f, 99.6f, 120f },
        [SensorQuantity.Fan] = new[] { 0f, 900f, 3400f, 8000f },
        [SensorQuantity.Frequency] = new[] { 0f, 850f, 999f, 4200f, 6000f },
        [SensorQuantity.Power] = new[] { 0f, 65f, 250f },
        [SensorQuantity.Load] = new[] { 0f, 42f, 100f },
        [SensorQuantity.Level] = new[] { 0f, 42f, 100f },
        [SensorQuantity.Control] = new[] { 0f, 42f, 100f },
        [SensorQuantity.Voltage] = new[] { 0f, 1.25f, 12f, 20f },
        [SensorQuantity.Data] = new[] { 0f, 512f, 999f, 1000f, 2048f, 4096f },
    };

    foreach (SensorQuantity quantity in Enum.GetValues<SensorQuantity>())
    {
        Check.True(ranges.ContainsKey(quantity),
            $"{quantity} has no tray width coverage; add its realistic range.");
        foreach (float sample in ranges[quantity])
        {
            string text = Format(quantity, sample);
            Check.True(text.Length <= 3,
                $"Tray glyphs must stay legible at 16 px; {quantity} at {sample} rendered '{text}'.");
        }
    }
}

static void TrayIconLayoutTests()
{
    // Legibility at 16 px rests on two renderer choices that are easy to "helpfully" undo:
    // whole-pixel hinting for small glyphs, and no contrast halo. Both were settled by comparing
    // rendered pixels — an 8-way rim composites to a near-opaque ring that closes glyph counters.
    FieldInfo? antiAliasMin = typeof(IconRenderer).GetField(
        "AntiAliasMinPx", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(antiAliasMin is not null,
        "The small-glyph hinting threshold should exist; grayscale AA smears a 7 px glyph.");
    Check.True((int)antiAliasMin!.GetRawConstantValue()! >= 10,
        "The hinting threshold must cover the sizes a 16 px canvas actually produces.");

    MethodInfo? hint = typeof(IconRenderer).GetMethod(
        "ApplyHintFor", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(hint is not null, "Per-size hint selection should exist.");

    // The halo field is gone; its return would reintroduce the mud this replaced.
    FieldInfo? halo = typeof(IconRenderer).GetField(
        "HaloColor", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(halo is null,
        "No contrast halo: it closed glyph counters and hurt legibility on both taskbar shades.");

    // Icons must render at the shell's small-icon size so the taskbar shows the pixels 1:1.
    FieldInfo? canvas = typeof(IconRenderer).GetField(
        "CanvasSize", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(canvas is not null, "Canvas size should be derived from the shell metric.");
    int size = (int)canvas!.GetValue(null)!;
    Check.True(size >= 16 && size <= 64, $"Canvas size {size} is outside the plausible range.");
}

static void LocalizationCoverageTests()
{
    // The primary user reads zh-TW, and Loc.T falls back to English (then to the raw key) rather
    // than throwing — so a missing translation is invisible in code review and only shows up as
    // stray English, or a literal "set.foo.bar", in the shipped UI. This check is what makes that
    // impossible to merge; it used to be an ad-hoc script run by hand.
    var locType = typeof(Loc);
    Dictionary<string, string> Dict(string field)
    {
        FieldInfo? f = locType.GetField(field, BindingFlags.Static | BindingFlags.NonPublic);
        Check.True(f is not null, $"Loc.{field} dictionary should exist.");
        return (Dictionary<string, string>)f!.GetValue(null)!;
    }

    Dictionary<string, string> en = Dict("En");
    Dictionary<string, string> zh = Dict("ZhTw");
    Check.True(en.Count > 200, $"English table looks truncated ({en.Count} keys).");

    // Every key must exist in BOTH tables, in both directions.
    var missingZh = new List<string>();
    foreach (string key in en.Keys)
        if (!zh.ContainsKey(key)) missingZh.Add(key);
    Check.Equal(0, missingZh.Count,
        "Keys missing a zh-TW translation: " + string.Join(", ", missingZh.Take(10)) + ".");

    var missingEn = new List<string>();
    foreach (string key in zh.Keys)
        if (!en.ContainsKey(key)) missingEn.Add(key);
    Check.Equal(0, missingEn.Count,
        "Keys present only in zh-TW: " + string.Join(", ", missingEn.Take(10)) + ".");

    // An empty value renders as a blank label, which is worse than an untranslated one.
    foreach ((string key, string value) in en)
        Check.True(!string.IsNullOrWhiteSpace(value), $"English value for '{key}' is blank.");
    foreach ((string key, string value) in zh)
        Check.True(!string.IsNullOrWhiteSpace(value), $"zh-TW value for '{key}' is blank.");

    // Format placeholders must agree, or Loc.F throws (or silently drops an argument) in one
    // language only — a class of bug that surfaces exclusively for the translated user.
    foreach ((string key, string value) in en)
    {
        int enSlots = CountFormatSlots(value);
        int zhSlots = CountFormatSlots(zh[key]);
        Check.Equal(enSlots, zhSlots,
            $"Format placeholder count differs for '{key}' (en={enSlots}, zh={zhSlots}).");
    }

    static int CountFormatSlots(string text)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '{') continue;
            if (text[i + 1] == '{') { i++; continue; }   // escaped brace
            int end = text.IndexOf('}', i + 1);
            if (end < 0) continue;
            string body = text[(i + 1)..end].Split(':')[0];
            if (int.TryParse(body, out int index)) seen.Add(index);
        }
        return seen.Count;
    }
}

static void AmbiguousSensorNameTests()
{
    // LibreHardwareMonitor gives a core's temperature, clock and power sensors the same Name, so
    // "P-Core #1" shows up several times. SensorService flags those collisions and the display
    // name gains a quantity suffix; names that are already unique must stay untouched.
    MethodInfo? mark = typeof(SensorService).GetMethod(
        "MarkAmbiguousNames", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(mark is not null, "Ambiguous-name detection should exist.");

    const string cpu = "13th Gen Intel Core i7-1360P";
    SensorDescriptor Cpu(string id, string name, SensorQuantity quantity) => new()
    {
        Id = id,
        HardwareName = cpu,
        Name = name,
        Category = SensorCategory.Cpu,
        Quantity = quantity,
    };

    var descriptors = new List<SensorDescriptor>
    {
        Cpu("/intelcpu/0/temperature/2", "P-Core #1", SensorQuantity.Temperature),
        Cpu("/intelcpu/0/clock/1", "P-Core #1", SensorQuantity.Frequency),
        Cpu("/intelcpu/0/temperature/14", "CPU Package", SensorQuantity.Temperature),
        Cpu("/intelcpu/0/power/0", "CPU Package", SensorQuantity.Power),
        Cpu("/intelcpu/0/temperature/0", "Core Max", SensorQuantity.Temperature),
        Cpu("/intelcpu/0/load/0", "CPU Total", SensorQuantity.Load),
        // Same name on DIFFERENT hardware is not a collision: each is unique in its own group.
        new()
        {
            Id = "/battery/0/voltage/0", HardwareName = "Primary", Name = "Voltage",
            Category = SensorCategory.Battery, Quantity = SensorQuantity.Voltage,
        },
    };

    mark!.Invoke(null, new object[] { descriptors });

    Check.True(descriptors[0].AmbiguousName, "A core temperature sharing its name must be flagged.");
    Check.True(descriptors[1].AmbiguousName, "A core clock sharing its name must be flagged.");
    Check.True(descriptors[2].AmbiguousName, "CPU Package temperature collides with its power sensor.");
    Check.True(descriptors[3].AmbiguousName, "CPU Package power collides with its temperature sensor.");
    Check.True(!descriptors[4].AmbiguousName, "A unique name must not be flagged.");
    Check.True(!descriptors[5].AmbiguousName, "A unique name must not be flagged.");
    Check.True(!descriptors[6].AmbiguousName, "Names only collide within one hardware.");

    var config = new AppConfig();
    Check.Equal("P-Core #1 (Temperature)", config.DisplayNameFor(descriptors[0]),
        "A flagged sensor should carry its quantity.");
    Check.Equal("P-Core #1 (Frequency)", config.DisplayNameFor(descriptors[1]),
        "The colliding sensor should carry a different quantity.");
    Check.Equal("CPU Package (Power)", config.DisplayNameFor(descriptors[3]),
        "Power collisions should be separated too.");
    Check.Equal("Core Max", config.DisplayNameFor(descriptors[4]),
        "Unambiguous names must stay clean.");
    Check.Equal("Voltage", config.DisplayNameFor(descriptors[6]),
        "A name unique to its hardware must stay clean.");

    // A user rename is their own disambiguation; appending to it would fight the user.
    config.SensorOverrides[descriptors[0].Id] = new SensorOverride { Rename = "CPU 第一核心" };
    Check.Equal("CPU 第一核心", config.DisplayNameFor(descriptors[0]),
        "A rename must be used verbatim, with no quantity appended.");
}

static void EcSensorComputeTests()
{
    var regs = new byte[256];
    var ok = new bool[256];

    // 0xB0/0xB1 is the LG 16T90R fan pair: DSDT RPM1/RPM2, little-endian direct RPM.
    regs[0xB0] = 0x48;
    regs[0xB1] = 0x0D;   // LE16 = 0x0D48 = 3400
    ok[0xB0] = true;
    ok[0xB1] = true;

    EcSensorDef Def(EcValueKind kind, int register = 0xB0) => new()
    {
        Register = register,
        Kind = kind,
        Name = "Fan",
        Quantity = SensorQuantity.Fan,
    };

    Check.Equal(3400f, Def(EcValueKind.RpmDirect).Compute(regs, ok) ?? -1f,
        "Little-endian word assembly should read the DSDT fan pair as RPM.");

    var bigEndian = Def(EcValueKind.RpmDirect);
    bigEndian.BigEndian = true;
    Check.Equal(18445f, bigEndian.Compute(regs, ok) ?? -1f,      // 0x480D
        "Big-endian mode must swap the byte order.");

    Check.Equal(72f, Def(EcValueKind.RawByte).Compute(regs, ok) ?? -1f,   // 0x48
        "RawByte should read the low register only.");

    var scaled = Def(EcValueKind.RawByte);
    scaled.Scale = 100f;
    scaled.Offset = 5f;
    Check.Equal(7205f, scaled.Compute(regs, ok) ?? -1f,          // 72 * 100 + 5
        "Scale and offset should apply to raw bytes.");

    // A stopped fan reports a zero period; dividing by it must not produce infinity.
    var stopped = new byte[256];
    var stoppedOk = new bool[256];
    stoppedOk[0xB0] = true;
    stoppedOk[0xB1] = true;
    var divided = Def(EcValueKind.RpmDivided);
    divided.Divisor = 1_000_000f;
    Check.Equal(0f, divided.Compute(stopped, stoppedOk) ?? -1f,
        "A zero period must read as a stopped fan, never infinity.");
    Check.True(float.IsFinite(divided.Compute(regs, ok) ?? float.NaN),
        "A live period must produce a finite RPM.");

    // A register the EC could not read this tick must surface as null, never a stale zero.
    var partial = new bool[256];
    partial[0xB0] = true;   // high byte missing
    Check.True(Def(EcValueKind.RpmDirect).Compute(regs, partial) is null,
        "A word sensor missing its high byte must return null.");
    Check.True(Def(EcValueKind.RawByte).Compute(regs, new bool[256]) is null,
        "An unread register must return null.");

    // The last register has no successor, so word kinds cannot be satisfied there.
    var lastOk = new bool[256];
    lastOk[0xFF] = true;
    Check.True(Def(EcValueKind.RpmDirect, 0xFF).Compute(regs, lastOk) is null,
        "A word sensor at register 0xFF has no high byte and must return null.");
}

static void ConfigMigrationTests()
{
    MethodInfo? migrate = typeof(ConfigStore).GetMethod(
        "Migrate", BindingFlags.Static | BindingFlags.NonPublic);
    MethodInfo? readVersion = typeof(ConfigStore).GetMethod(
        "ReadSchemaVersion", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(migrate is not null, "Config migration implementation should exist.");
    Check.True(readVersion is not null, "Schema version reader should exist.");

    int Version(JsonObject root) => (int)readVersion!.Invoke(null, new object[] { root })!;

    Check.Equal(1, Version(new JsonObject()),
        "A document without SchemaVersion is a v1 document.");
    Check.Equal(1, Version(new JsonObject { ["SchemaVersion"] = "four" }),
        "A non-numeric SchemaVersion must not be trusted.");
    Check.Equal(1, Version(new JsonObject { ["SchemaVersion"] = 0 }),
        "A SchemaVersion below 1 must not be trusted.");
    Check.Equal(3, Version(new JsonObject { ["SchemaVersion"] = 3 }),
        "A valid SchemaVersion should be read as-is.");

    // Walk the whole chain the way Load does: every step must run without throwing and must
    // leave a document the typed contract can still deserialize. This is the guard that keeps a
    // future migration from silently wiping settings for users upgrading from an older build.
    var legacy = new JsonObject
    {
        ["SchemaVersion"] = 1,
        ["PollIntervalMs"] = 2000,
        ["Language"] = "zh-TW",
        ["UseFahrenheit"] = true,
    };

    int version = Version(legacy);
    int steps = 0;
    while (version < 4)
    {
        migrate!.Invoke(null, new object[] { legacy, version });
        version++;
        legacy["SchemaVersion"] = version;
        Check.True(++steps <= 16, "Migration chain should terminate.");
    }

    Check.Equal(4, Version(legacy), "The chain should land on the current schema version.");

    AppConfig? migrated = legacy.Deserialize<AppConfig>(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    });
    Check.True(migrated is not null, "A migrated document must still deserialize.");
    Check.Equal("zh-TW", migrated!.Language, "Migration must preserve user settings it does not reshape.");
    Check.True(migrated.UseFahrenheit, "Migration must preserve unrelated user settings.");
}

static void HistoryLayoutFingerprintTests()
{
    MethodInfo? method = typeof(HistoryLogger).GetMethod(
        "BuildLayoutFingerprint", BindingFlags.Static | BindingFlags.NonPublic);
    Check.True(method is not null, "History logger layout fingerprint implementation should exist.");

    SensorDescriptor first = Descriptor("/storage/0/temperature", "Temperature");
    SensorDescriptor sameHeaderDifferentId = Descriptor("/storage/1/temperature", "Temperature");
    SensorDescriptor second = Descriptor("/storage/0/used", "Used Space");

    string Fingerprint(params SensorDescriptor[] descriptors)
        => (string)method!.Invoke(null, new object[] { descriptors })!;

    string original = Fingerprint(first, second);
    Check.True(!string.Equals(original, Fingerprint(sameHeaderDifferentId, second), StringComparison.Ordinal),
        "Matching CSV headers with a different sensor id must use a different log layout.");
    Check.True(!string.Equals(original, Fingerprint(second, first), StringComparison.Ordinal),
        "Changing column order must use a different log layout.");
}

static SensorSnapshot Sample(string id, float value, DateTime utc) => new()
{
    Id = id,
    Value = value,
    UtcTimestamp = utc,
};

static SensorDescriptor Descriptor(string id, string name) => new()
{
    Id = id,
    HardwareName = "Storage",
    Name = name,
    DisplayName = name,
    Category = SensorCategory.Storage,
    Quantity = SensorQuantity.Temperature,
};

/// <summary>
/// Points ConfigStore at a scratch directory for one test. The real config lives in %AppData% and
/// belongs to the person running the harness; no check may read or write it.
/// </summary>
sealed class ScopedConfigDirectory : IDisposable
{
    private static readonly FieldInfo Backing = ResolveBackingField();
    private readonly string _previous;

    public string Path { get; }

    public ScopedConfigDirectory()
    {
        _previous = ConfigStore.ConfigDirectory;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "WinMonitor-cfgtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Backing.SetValue(null, Path);
    }

    public void Dispose()
    {
        Backing.SetValue(null, _previous);
        // The directory was just written to, so a scanner or indexer can still hold it briefly;
        // one retry keeps the harness from littering %TEMP% with empty folders.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(Path, recursive: true); return; }
            catch { Thread.Sleep(50); }
        }
    }

    private static FieldInfo ResolveBackingField()
    {
        FieldInfo? field = typeof(ConfigStore).GetField(
            "<ConfigDirectory>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        // Fail loudly: silently falling back would run these checks against the real config.
        if (field is null)
            throw new InvalidOperationException("ConfigStore.ConfigDirectory backing field not found; " +
                "redirection is required so the harness never touches the real config.");
        return field;
    }
}

/// <summary>
/// Stands in for the app's SyncWindow: the harness has no message loop, so marshaled work runs
/// inline. InvokeRequired is false, which is exactly the state TrayIconManager.Rebuild takes as
/// "already on the UI thread".
/// </summary>
sealed class ImmediateSync : ISynchronizeInvoke
{
    public bool InvokeRequired => false;

    public IAsyncResult BeginInvoke(Delegate method, object?[]? args)
    {
        method.DynamicInvoke(args);
        return new Completed();
    }

    public object? EndInvoke(IAsyncResult result) => null;

    public object? Invoke(Delegate method, object?[]? args) => method.DynamicInvoke(args);

    private sealed class Completed : IAsyncResult
    {
        public object? AsyncState => null;
        public WaitHandle AsyncWaitHandle => new ManualResetEvent(true);
        public bool CompletedSynchronously => true;
        public bool IsCompleted => true;
    }
}

static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException($"{message} Expected {expected}; actual {actual}.");
    }

    public static void Sequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message) where T : IEquatable<T>
    {
        if (expected.Count != actual.Count)
            throw new InvalidOperationException($"{message} Expected {expected.Count} entries; actual {actual.Count}.");
        for (int i = 0; i < expected.Count; i++)
        {
            if (!expected[i].Equals(actual[i]))
                throw new InvalidOperationException($"{message} Entries differ at index {i}.");
        }
    }
}
