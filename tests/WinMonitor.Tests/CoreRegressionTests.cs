using System.Reflection;
using WinMonitor.Config;
using WinMonitor.Core;

var tests = new (string Name, Action Run)[]
{
    (nameof(StatsTrackerHistoryTests), StatsTrackerHistoryTests),
    (nameof(ConfigSanitizationTests), ConfigSanitizationTests),
    (nameof(SuppressedStorageReferenceTests), SuppressedStorageReferenceTests),
    (nameof(ProfileAlertTests), ProfileAlertTests),
    (nameof(HistoryLayoutFingerprintTests), HistoryLayoutFingerprintTests),
    (nameof(CsvExportTests), CsvExportTests),
    (nameof(IntelThermalStatusDecodeTests), IntelThermalStatusDecodeTests),
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
    var tracker = new StatsTracker(historyCapacity: 3);

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
        var histories = new Dictionary<string, IReadOnlyList<TimedValue>>(StringComparer.Ordinal)
        {
            [descriptor.Id] = new[]
            {
                new TimedValue(first, 1.5f),
                new TimedValue(first.AddSeconds(1), 3.5f),
            },
            [throttle.Id] = new[]
            {
                new TimedValue(first, 0f),
                new TimedValue(first.AddSeconds(1), 1f),
            },
        };

        HistoryLogger.ExportTimeSeriesCsv(
            path,
            new[] { descriptor, throttle },
            id => histories.TryGetValue(id, out IReadOnlyList<TimedValue>? values)
                ? values
                : Array.Empty<TimedValue>());
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
