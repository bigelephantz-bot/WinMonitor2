using System.Text.Json.Serialization;
using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.Config;

/// <summary>
/// Root JSON config. Everything is plain mutable POCO for System.Text.Json.
/// Persisted by ConfigStore (atomic write). Keep defaults sensible: the app must be
/// useful on first launch with zero configuration.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// On-disk schema version. Bump together with ConfigStore.CurrentSchemaVersion and
    /// add a step in ConfigStore.Migrate — that is where migrations live.
    /// </summary>
    public int SchemaVersion { get; set; } = 4;

    // ---- General ----
    public string Language { get; set; } = "auto";        // "auto" | "en" | "zh-TW"
    public bool UseFahrenheit { get; set; }
    public int PollIntervalMs { get; set; } = 2000;       // 1000 / 2000 / 5000
    public bool StartWithWindows { get; set; }
    public int StartupDelaySeconds { get; set; }          // 0..60
    public bool StartMinimized { get; set; }              // sensor-only start (tray only)
    public bool CloseToTray { get; set; } = true;
    public bool ConfirmOnClose { get; set; }              // ask before hiding/exiting on X
    public bool CompactMode { get; set; }                 // last used window mode
    public System.Drawing.Point? CompactLocation { get; set; }
    public System.Drawing.Rectangle? MainWindowBounds { get; set; }
    public int ChartMinutes { get; set; } = 10;           // 1 / 3 / 5 / 10 / 20 / 30 / 60

    // ---- Polling behavior ----
    /// <summary>On battery, multiply the poll interval to save power.</summary>
    public bool BatteryAdaptivePolling { get; set; } = true;
    /// <summary>Interval multiplier applied while on battery when adaptive polling is on.</summary>
    public int BatteryPollMultiplier { get; set; } = 2;
    /// <summary>Read the Embedded Controller only every N poll ticks (re-emit last values between).</summary>
    public int EcReadEveryNTicks { get; set; } = 2;

    // ---- Sensor display ----
    /// <summary>Show individual per-core CPU temperatures in the main window.</summary>
    public bool ShowPerCoreTemps { get; set; }
    /// <summary>Flag ACPI thermal zones whose value never changes as likely static/placeholder.</summary>
    public bool FlagStaticZones { get; set; } = true;

    // ---- Appearance ----
    /// <summary>"auto" (follow Windows), "light", "dark".</summary>
    public string ThemeMode { get; set; } = "auto";

    // ---- Chart ----
    /// <summary>Sensor ids checked for charting in the main window (persisted across restarts).</summary>
    public List<string> ChartSensorIds { get; set; } = new();

    // ---- Global hotkey (toggle compact overlay) ----
    public bool HotkeyEnabled { get; set; } = true;
    /// <summary>Win32 MOD_* flags: 1=Alt, 2=Ctrl, 4=Shift, 8=Win. Default Ctrl+Alt.</summary>
    public int HotkeyModifiers { get; set; } = 0x2 | 0x1;
    /// <summary>Virtual-key code. Default 'M' (0x4D).</summary>
    public int HotkeyKey { get; set; } = 0x4D;

    // ---- Peaks auto reset ----
    public bool AutoResetPeaksDaily { get; set; }
    /// <summary>"HH:mm" local time for the daily reset when AutoResetPeaksDaily is on.</summary>
    public string AutoResetTime { get; set; } = "00:00";
    public bool ResetPeaksOnResume { get; set; }

    // ---- CPU throttle detection ----
    public bool ThrottleIndicatorEnabled { get; set; } = true;
    public bool ThrottleToast { get; set; } = true;
    /// <summary>Seconds the throttle condition must persist before the state flips (debounce).</summary>
    public int ThrottleSustainSeconds { get; set; } = 5;

    // ---- Sensors (global, shared by all profiles) ----
    /// <summary>Key = sensor Id. Rename / hide / custom thresholds.</summary>
    public Dictionary<string, SensorOverride> SensorOverrides { get; set; } = new();

    // ---- Profiles ----
    public string ActiveProfile { get; set; } = "Default";
    public List<Profile> Profiles { get; set; } = new() { Profile.CreateDefault("Default") };

    // ---- Logging ----
    public LoggingConfig Logging { get; set; } = new();

    // ---- Embedded Controller (LG fan support via PawnIO) ----
    public EcConfig Ec { get; set; } = new();

    [JsonIgnore]
    public Profile Active =>
        Profiles.Find(p => p.Name == ActiveProfile) ?? (Profiles.Count > 0 ? Profiles[0] : Profile.CreateDefault("Default"));

    /// <summary>Resolve effective thresholds for a sensor: profile override → global override → suggested.</summary>
    public Thresholds ResolveThresholds(SensorDescriptor d)
    {
        if (Active.ThresholdOverrides.TryGetValue(d.Id, out var p) && p is not null) return p;
        if (SensorOverrides.TryGetValue(d.Id, out var o) && o.Thresholds is not null) return o.Thresholds;
        return Thresholds.SuggestFor(d.Category, d.Quantity);
    }

    public string DisplayNameFor(SensorDescriptor d)
    {
        // A user rename is taken exactly as typed: they already disambiguated it themselves.
        if (SensorOverrides.TryGetValue(d.Id, out var o) && !string.IsNullOrWhiteSpace(o.Rename))
            return o.Rename!;

        string name = d.Name;

        // Storage sensor names ("Temperature", "Used Space"...) are ambiguous when several
        // drives are present, so default to "<model> <sensor>" (HardwareName is the model
        // string from LHM, e.g. "Samsung SSD 990 PRO 2TB"). Flows to tooltips/alerts/logs.
        if (d.Category == SensorCategory.Storage
            && d.HardwareName.Length > 0
            && !d.Name.StartsWith(d.HardwareName, StringComparison.Ordinal))
            name = d.HardwareName + " " + d.Name;

        // LibreHardwareMonitor gives a core's temperature, clock and power sensors the same name,
        // so "P-Core #1" can appear three times. SensorService flags those collisions; appending
        // the quantity is what separates them. Names with no collision are left untouched.
        if (d.AmbiguousName)
            name += " (" + Loc.T(QuantityKey(d.Quantity)) + ")";

        return name;
    }

    /// <summary>Localization key naming a quantity, used to disambiguate repeated sensor names.</summary>
    private static string QuantityKey(SensorQuantity quantity) => quantity switch
    {
        SensorQuantity.Temperature => "quantity.temperature",
        SensorQuantity.Fan => "quantity.fan",
        SensorQuantity.Control => "quantity.control",
        SensorQuantity.Level => "quantity.level",
        SensorQuantity.Power => "quantity.power",
        SensorQuantity.Data => "quantity.data",
        SensorQuantity.Voltage => "quantity.voltage",
        SensorQuantity.Load => "quantity.load",
        SensorQuantity.Frequency => "quantity.frequency",
        _ => "quantity.other",
    };

    public bool IsHidden(string sensorId)
        => SensorOverrides.TryGetValue(sensorId, out var o) && o.Hidden;
}

public sealed class SensorOverride
{
    public string? Rename { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Pinned sensors show in the Favorites group at the top of the main list.</summary>
    public bool Pinned { get; set; }
    public Thresholds? Thresholds { get; set; }
}

/// <summary>Green/yellow/red banding + alert options. Values in the sensor's native unit (°C, RPM, %...).</summary>
public sealed class Thresholds
{
    public float Yellow { get; set; } = 70;
    public float Red { get; set; } = 85;
    public bool AlertEnabled { get; set; }
    public int SustainSeconds { get; set; } = 5;   // value must exceed Red this long before alerting
    public bool PlaySound { get; set; }
    public string? SoundPath { get; set; }         // null → system exclamation sound

    public Thresholds Clone() => (Thresholds)MemberwiseClone();

    /// <summary>Conservative suggested bands per category. Used when the user never set anything.</summary>
    public static Thresholds SuggestFor(SensorCategory category, SensorQuantity quantity)
    {
        if (quantity == SensorQuantity.Temperature)
        {
            return category switch
            {
                SensorCategory.Cpu => new Thresholds { Yellow = 80, Red = 95 },
                SensorCategory.Gpu => new Thresholds { Yellow = 80, Red = 92 },
                SensorCategory.Storage => new Thresholds { Yellow = 60, Red = 70 },
                SensorCategory.Battery => new Thresholds { Yellow = 40, Red = 50 },
                SensorCategory.Memory => new Thresholds { Yellow = 60, Red = 75 },
                _ => new Thresholds { Yellow = 70, Red = 85 },
            };
        }
        // Non-temperature quantities: bands mostly meaningless; keep alert off and bands high.
        return quantity switch
        {
            SensorQuantity.Fan => new Thresholds { Yellow = 5500, Red = 7000 },
            SensorQuantity.Load or SensorQuantity.Control => new Thresholds { Yellow = 85, Red = 95 },
            SensorQuantity.Level => new Thresholds { Yellow = 101, Red = 101 },  // never colored
            SensorQuantity.Power => new Thresholds { Yellow = 45, Red = 64 },
            _ => new Thresholds { Yellow = float.MaxValue, Red = float.MaxValue },
        };
    }
}

/// <summary>A named set of tray icons + per-profile threshold overrides ("日常" / "遊戲" / "安靜"...).</summary>
public sealed class Profile
{
    public string Name { get; set; } = "Default";
    public List<TrayIconConfig> TrayIcons { get; set; } = new();
    /// <summary>Key = sensor Id; overrides global thresholds while this profile is active.</summary>
    public Dictionary<string, Thresholds?> ThresholdOverrides { get; set; } = new();

    public static Profile CreateDefault(string name) => new()
    {
        Name = name,
        // Empty sensor list = "auto": TrayIconManager substitutes the hottest CPU package temp
        // sensor on first run so the app shows something useful before any configuration.
        TrayIcons = new List<TrayIconConfig> { new() },
    };

    public Profile Clone(string newName)
    {
        var p = new Profile { Name = newName };
        foreach (var t in TrayIcons) p.TrayIcons.Add(t.Clone());
        foreach (var (k, v) in ThresholdOverrides) p.ThresholdOverrides[k] = v?.Clone();
        return p;
    }
}

public enum TrayIconStyle
{
    TextOnly,       // colored digits on transparent background
    TextOnBadge,    // white digits on colored rounded badge
}

public sealed class TrayIconConfig
{
    /// <summary>Sensor ids shown by this icon. 1 = static; more = carousel. Empty = auto (CPU).</summary>
    public List<string> SensorIds { get; set; } = new();
    public int RotateIntervalSec { get; set; } = 3;
    public TrayIconStyle Style { get; set; } = TrayIconStyle.TextOnly;
    /// <summary>Show the quantity unit in the tray glyph (for example, °C, W, or %).</summary>
    public bool ShowUnit { get; set; } = true;
    public bool Bold { get; set; } = true;
    /// <summary>HTML color like "#RRGGBB"; null → threshold colors (green/yellow/red).</summary>
    public string? ColorOverride { get; set; }
    /// <summary>Draw a small history sparkline behind the tray glyph.</summary>
    public bool ShowSparkline { get; set; }

    public TrayIconConfig Clone() => new()
    {
        SensorIds = new List<string>(SensorIds),
        RotateIntervalSec = RotateIntervalSec,
        Style = Style,
        ShowUnit = ShowUnit,
        Bold = Bold,
        ColorOverride = ColorOverride,
        ShowSparkline = ShowSparkline,
    };
}

public sealed class LoggingConfig
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 30;
    public int RetentionDays { get; set; } = 14;
}
