using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Tray;

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
    (nameof(EcSensorComputeTests), EcSensorComputeTests),
    (nameof(ConfigMigrationTests), ConfigMigrationTests),
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

    const string lateChartId = "/cpu/package/frequency/0";
    for (int i = 0; i < 4; i++)
        tracker.Accept(new[] { Sample(lateChartId, 1000f + i, firstTime.AddSeconds(3 + i)) });
    HistoryReadResult late = tracker.GetHistoryIfChanged(lateChartId, -1);
    Check.True(late.Values is { Length: 3 },
        "A chart opened later should receive the existing bounded history immediately.");
    Check.Equal(1001f, late.Values![0].Value, "Late chart history should retain the oldest in-capacity sample.");
    Check.Equal(1003f, late.Values[2].Value, "Late chart history should include the newest sample.");
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
    logger.Accept(Array.Empty<SensorSnapshot>());

    Check.True(writerField.GetValue(logger) is null,
        "Disabling logging should immediately release the open writer.");
    Check.True(!stream.CanWrite, "Disabling logging should close the underlying file stream.");
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
