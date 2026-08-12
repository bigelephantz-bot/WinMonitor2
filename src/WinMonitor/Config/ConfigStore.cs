using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WinMonitor.Core;

namespace WinMonitor.Config;

/// <summary>
/// Persists <see cref="AppConfig"/> as config.json in one of two locations:
/// portable mode (next to the exe when "portable.txt" or an existing config.json sits
/// there) or %AppData%\WinMonitor. Saves are atomic (write .tmp, then File.Replace),
/// so a crash mid-save can never corrupt the previous file.
/// </summary>
public static class ConfigStore
{
    private const string FileName = "config.json";
    private const string PortableMarker = "portable.txt";

    // Keep in sync with the AppConfig.SchemaVersion initializer. Bumping it requires a
    // matching case in Migrate below.
    private const int CurrentSchemaVersion = 4;

    private static readonly object SaveLock = new();
    private static readonly JsonSerializerOptions Options = CreateOptions();

    // Set when Load hit a persistent transient read error: the on-disk file may be
    // intact, so Save diverts to config.json.recovered instead of clobbering it.
    private static bool _loadFailed;

    // Set when config.json was produced by a newer schema. The old build may use the fields it
    // understands, but it must never overwrite the newer document and discard future fields.
    private static bool _loadedNewerSchema;

    public static string ConfigDirectory { get; }
    public static bool IsPortable { get; }
    public static bool IsLoadedFromNewerSchema => _loadedNewerSchema;

    // Static ctor must never throw: Program.Main and the crash logger touch this type
    // before any error UI exists. Directory creation is deferred to Load/Save.
    static ConfigStore()
    {
        string exeDir = AppContext.BaseDirectory;

        bool portable;
        try
        {
            portable = File.Exists(Path.Combine(exeDir, PortableMarker))
                    || File.Exists(Path.Combine(exeDir, FileName));
        }
        catch
        {
            portable = false;
        }

        string dir;
        if (portable)
        {
            dir = exeDir;
        }
        else
        {
            string appData;
            try { appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
            catch { appData = ""; }
            dir = appData.Length == 0 ? exeDir : Path.Combine(appData, "WinMonitor");
        }

        IsPortable = portable;
        ConfigDirectory = dir;
    }

    /// <summary>
    /// Returns defaults when the file is missing; backs up a corrupt file as
    /// config.json.bak and returns defaults. A transient read error (AV scan, lock)
    /// is retried once; if it persists, defaults are returned but the file is left
    /// untouched and Save diverts to config.json.recovered for this session.
    /// Files with an older SchemaVersion are migrated in place (see Migrate) before
    /// typed deserialization.
    /// </summary>
    public static AppConfig Load()
    {
        string path = Path.Combine(ConfigDirectory, FileName);
        AppConfig config;
        _loadFailed = false;
        _loadedNewerSchema = false;
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            if (File.Exists(path))
            {
                string json;
                try
                {
                    json = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                    json = File.ReadAllText(path);
                }
                // Migrate the raw JSON before typed deserialization so shape changes
                // between schema versions never hit the AppConfig contract directly.
                // A parse failure here throws JsonException -> corrupt path below.
                JsonNode? node = JsonNode.Parse(json);
                if (node is JsonObject root)
                {
                    // Ordered chain: each Migrate call moves one version forward; the
                    // loop bumps SchemaVersion centrally, so cases only reshape data.
                    // A newer document is read best-effort for this session, but Save is
                    // redirected to a recovery sibling so future fields cannot be erased.
                    int version = ReadSchemaVersion(root);
                    _loadedNewerSchema = version > CurrentSchemaVersion;
                    if (_loadedNewerSchema)
                    {
                        Diag.Log("config", "File was written by a newer schema (v" + version
                            + " > v" + CurrentSchemaVersion + "); saves divert to a recovery sibling");
                    }
                    if (version < CurrentSchemaVersion)
                        Diag.Log("config", "Migrating schema v" + version + " -> v" + CurrentSchemaVersion);
                    while (version < CurrentSchemaVersion)
                    {
                        Migrate(root, version);
                        version++;
                        root["SchemaVersion"] = version;
                    }
                    config = root.Deserialize<AppConfig>(Options) ?? new AppConfig();
                }
                else
                {
                    // "null" or a non-object document: same handling as pre-migration
                    // (null -> defaults; anything else throws -> corrupt path below).
                    config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
                }
            }
            else
            {
                config = new AppConfig();
            }
        }
        catch (IOException ex)
        {
            // The file may be perfectly fine; do not back it up or overwrite it.
            _loadFailed = true;
            config = new AppConfig();
            Diag.Log("config", "Config unreadable (transient); running on defaults and diverting saves", ex);
        }
        catch (JsonException ex) when (_loadedNewerSchema)
        {
            // An older build cannot prove this is corrupt: a newer schema may have changed a
            // known property's shape. Preserve the source and divert any later save instead.
            _loadFailed = true;
            config = new AppConfig();
            Diag.Log("config", "Newer-schema config could not be parsed; source left untouched", ex);
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable: keep the evidence, start from defaults.
            TryBackupCorrupt(path);
            config = new AppConfig();
            Diag.Log("config", "Config corrupt; backed up to config.json.bak and reset to defaults", ex);
        }

        // A recovery sibling always uses this build's schema; the newer source file remains
        // untouched when _loadedNewerSchema is true.
        config.SchemaVersion = CurrentSchemaVersion;
        Sanitize(config);
        if (!_loadedNewerSchema)
            KnownEcProfiles.ApplyOnce(config.Ec);
        return config;
    }

    /// <summary>Reads SchemaVersion from the raw document; missing/invalid counts as 1.</summary>
    private static int ReadSchemaVersion(JsonObject root)
    {
        if (root.TryGetPropertyValue("SchemaVersion", out JsonNode? node)
            && node is JsonValue value
            && value.TryGetValue(out int version)
            && version >= 1)
        {
            return version;
        }
        return 1;
    }

    /// <summary>
    /// Applies the one-step migration from <paramref name="fromVersion"/> to the next
    /// version. Load walks the chain in order, so each case only needs to know the
    /// shape of the version directly before it; the loop in Load bumps SchemaVersion
    /// centrally after each step.
    /// </summary>
    private static void Migrate(JsonObject root, int fromVersion)
    {
        switch (fromVersion)
        {
            case 1:
                // v1 -> v2 changed nothing structurally. This deliberate no-op is the
                // exercised example: every pre-existing v1 config on disk walks through
                // here exactly once, proving the migration seam works end to end.
                break;
            case 2:
                // v2 -> v3 adds Ec.AppliedDefaultProfile. The missing member naturally
                // deserializes as null; KnownEcProfiles.ApplyOnce handles the one-time,
                // exact-model default after the typed config has been sanitized.
                break;
            case 3:
                // v3 -> v4 makes tray units visible by default. Existing entries carried
                // the former false default explicitly, so update each one here; users can
                // still turn an individual icon's unit off afterwards in Settings.
                if (root["Profiles"] is not JsonArray profiles) break;
                foreach (JsonNode? profileNode in profiles)
                {
                    if (profileNode is not JsonObject profile
                        || profile["TrayIcons"] is not JsonArray icons) continue;
                    foreach (JsonNode? iconNode in icons)
                        if (iconNode is JsonObject icon) icon["ShowUnit"] = true;
                }
                break;
        }
    }

    /// <summary>
    /// Atomic save. Serialize to a temporary file, then swap it in. A transient IOException
    /// (AV scan, indexer) is retried once after 100 ms. When a newer schema was loaded, the
    /// known-field recovery copy is written as config.json.newer-version instead of replacing it.
    /// </summary>
    public static void Save(AppConfig config)
    {
        lock (SaveLock)
        {
            try
            {
                SaveCore(config);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
                SaveCore(config);
            }
        }
    }

    private static void SaveCore(AppConfig config)
    {
        Sanitize(config);
        config.SchemaVersion = CurrentSchemaVersion;
        Directory.CreateDirectory(ConfigDirectory);
        string name = _loadFailed ? FileName + ".recovered"
            : _loadedNewerSchema ? FileName + ".newer-version"
            : FileName;
        string path = Path.Combine(ConfigDirectory, name);
        string tmp = path + ".tmp";

        string json = JsonSerializer.Serialize(config, Options);
        File.WriteAllText(tmp, json);

        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.Move(path, path + ".bak", overwrite: true);
        }
        catch
        {
            // Backup is best-effort; defaults are returned regardless.
        }
    }

    /// <summary>
    /// Normalizes a config loaded from disk or assembled by another caller. Keep this public so
    /// diagnostics and regression tests can exercise exactly the same bounds as ConfigStore.Load.
    /// </summary>
    public static void Sanitize(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.PollIntervalMs = NormalizeToOptions(config.PollIntervalMs, 1000, 2000, 5000);
        config.ChartMinutes = NormalizeToOptions(config.ChartMinutes, 1, 3, 5, 10, 20, 30, 60);
        config.StartupDelaySeconds = Math.Clamp(config.StartupDelaySeconds, 0, 60);
        config.BatteryPollMultiplier = Math.Clamp(config.BatteryPollMultiplier, 1, 10);
        config.EcReadEveryNTicks = Math.Clamp(config.EcReadEveryNTicks, 1, 10);
        config.ThrottleSustainSeconds = Math.Clamp(config.ThrottleSustainSeconds, 0, 3600);
        config.HotkeyModifiers &= 0x0F;
        if (config.HotkeyModifiers == 0) config.HotkeyModifiers = 0x2 | 0x1;
        if (config.HotkeyKey is < 1 or > 254) config.HotkeyKey = 0x4D;
        config.Language = NormalizeLanguage(config.Language);
        config.ThemeMode = NormalizeThemeMode(config.ThemeMode);
        if (!TimeSpan.TryParseExact(config.AutoResetTime, @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan autoResetTime)
            || autoResetTime < TimeSpan.Zero || autoResetTime >= TimeSpan.FromDays(1))
            config.AutoResetTime = "00:00";

        config.ChartSensorIds ??= new List<string>();
        RemoveEmptyAndDuplicateIds(config.ChartSensorIds);

        config.SensorOverrides ??= new Dictionary<string, SensorOverride>();
        foreach (string key in config.SensorOverrides.Keys.ToArray())
        {
            if (string.IsNullOrWhiteSpace(key) || config.SensorOverrides[key] is null)
            {
                config.SensorOverrides.Remove(key);
                continue;
            }
            if (config.SensorOverrides[key].Thresholds is { } thresholds)
                SanitizeThresholds(thresholds);
        }

        config.Logging ??= new LoggingConfig();
        config.Logging.IntervalSeconds = Math.Clamp(config.Logging.IntervalSeconds, 5, 3600);
        config.Logging.RetentionDays = Math.Clamp(config.Logging.RetentionDays, 1, 365);

        config.Ec ??= new EcConfig();
        config.Ec.Sensors ??= new List<EcSensorDef>();
        config.Ec.Sensors.RemoveAll(s => s is null || (uint)s.Register > 0xFF);
        for (int i = 0; i < config.Ec.Sensors.Count; i++)
            SanitizeEcSensor(config.Ec.Sensors[i]);

        config.Profiles ??= new List<Profile>();
        config.Profiles.RemoveAll(p => p is null);
        if (config.Profiles.Count == 0)
            config.Profiles.Add(Profile.CreateDefault("Default"));

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < config.Profiles.Count; i++)
        {
            var p = config.Profiles[i];
            p.Name = MakeUniqueProfileName(p.Name, i + 1, profileNames);
            p.TrayIcons ??= new List<TrayIconConfig>();
            p.TrayIcons.RemoveAll(t => t is null);
            p.ThresholdOverrides ??= new Dictionary<string, Thresholds?>();
            foreach (string key in p.ThresholdOverrides.Keys.ToArray())
            {
                Thresholds? thresholds = p.ThresholdOverrides[key];
                if (string.IsNullOrWhiteSpace(key) || thresholds is null)
                {
                    p.ThresholdOverrides.Remove(key);
                    continue;
                }
                SanitizeThresholds(thresholds);
            }

            for (int j = 0; j < p.TrayIcons.Count; j++)
            {
                TrayIconConfig tray = p.TrayIcons[j];
                tray.SensorIds ??= new List<string>();
                RemoveEmptyAndDuplicateIds(tray.SensorIds);
                tray.RotateIntervalSec = Math.Clamp(tray.RotateIntervalSec, 1, 60);
                if (!Enum.IsDefined(typeof(TrayIconStyle), tray.Style)) tray.Style = TrayIconStyle.TextOnly;
            }
        }

        bool activeExists = false;
        for (int i = 0; i < config.Profiles.Count; i++)
        {
            if (string.Equals(config.Profiles[i].Name, config.ActiveProfile, StringComparison.Ordinal))
            {
                activeExists = true;
                break;
            }
        }
        if (!activeExists) config.ActiveProfile = config.Profiles[0].Name;
    }

    /// <summary>
    /// Removes references to the fixed NVMe warning/critical temperature sensors deliberately
    /// suppressed by <see cref="SensorService"/>. Call only after a descriptor build and
    /// pass that service's exact suppressed-id list; arbitrary missing sensors must be retained
    /// because a disconnected device can legitimately return later. Returns true when the caller
    /// should persist the cleaned config.
    /// </summary>
    public static bool PruneSuppressedStorageTemperatureLimitReferences(
        AppConfig config, IReadOnlyCollection<string> suppressedSensorIds)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(suppressedSensorIds);

        var obsolete = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in suppressedSensorIds)
            if (!string.IsNullOrWhiteSpace(id)) obsolete.Add(id);
        if (obsolete.Count == 0) return false;

        bool changed = false;
        if (config.ChartSensorIds is not null)
            changed |= RemoveIds(config.ChartSensorIds, obsolete);

        if (config.SensorOverrides is not null)
        {
            foreach (string id in config.SensorOverrides.Keys.ToArray())
            {
                if (!obsolete.Contains(id)) continue;
                config.SensorOverrides.Remove(id);
                changed = true;
            }
        }

        if (config.Profiles is null) return changed;
        for (int p = 0; p < config.Profiles.Count; p++)
        {
            Profile? profile = config.Profiles[p];
            if (profile is null) continue;

            if (profile.ThresholdOverrides is not null)
            {
                foreach (string id in profile.ThresholdOverrides.Keys.ToArray())
                {
                    if (!obsolete.Contains(id)) continue;
                    profile.ThresholdOverrides.Remove(id);
                    changed = true;
                }
            }

            if (profile.TrayIcons is null) continue;
            for (int i = profile.TrayIcons.Count - 1; i >= 0; i--)
            {
                TrayIconConfig? tray = profile.TrayIcons[i];
                if (tray?.SensorIds is not { Count: > 0 } ids) continue;

                int countBefore = ids.Count;
                if (!RemoveIds(ids, obsolete)) continue;
                changed = true;
                // An explicitly selected legacy sensor should not silently turn into an
                // automatic CPU icon when it was the only member of the carousel.
                if (countBefore > 0 && ids.Count == 0)
                    profile.TrayIcons.RemoveAt(i);
            }
        }
        return changed;
    }

    private static int NormalizeToOptions(int value, params int[] options)
    {
        int nearest = options[0];
        long smallestDifference = Math.Abs((long)value - nearest);
        for (int i = 1; i < options.Length; i++)
        {
            long difference = Math.Abs((long)value - options[i]);
            if (difference >= smallestDifference) continue;
            nearest = options[i];
            smallestDifference = difference;
        }
        return nearest;
    }

    private static string NormalizeLanguage(string? language)
        => string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en"
            : string.Equals(language, "zh-TW", StringComparison.OrdinalIgnoreCase) ? "zh-TW"
            : "auto";

    private static string NormalizeThemeMode(string? themeMode)
        => string.Equals(themeMode, "light", StringComparison.OrdinalIgnoreCase) ? "light"
            : string.Equals(themeMode, "dark", StringComparison.OrdinalIgnoreCase) ? "dark"
            : "auto";

    private static void RemoveEmptyAndDuplicateIds(List<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ids.Count;)
        {
            string? id = ids[i];
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                ids.RemoveAt(i);
            else
                i++;
        }
    }

    private static bool RemoveIds(List<string> ids, HashSet<string> idsToRemove)
    {
        bool changed = false;
        for (int i = ids.Count - 1; i >= 0; i--)
        {
            if (!idsToRemove.Contains(ids[i])) continue;
            ids.RemoveAt(i);
            changed = true;
        }
        return changed;
    }

    private static void SanitizeThresholds(Thresholds thresholds)
    {
        if (!float.IsFinite(thresholds.Yellow)) thresholds.Yellow = 0;
        if (!float.IsFinite(thresholds.Red)) thresholds.Red = thresholds.Yellow;
        if (thresholds.Red < thresholds.Yellow) thresholds.Red = thresholds.Yellow;
        thresholds.SustainSeconds = Math.Clamp(thresholds.SustainSeconds, 0, 3600);
        if (string.IsNullOrWhiteSpace(thresholds.SoundPath)) thresholds.SoundPath = null;
    }

    private static void SanitizeEcSensor(EcSensorDef sensor)
    {
        if (!Enum.IsDefined(typeof(EcValueKind), sensor.Kind)) sensor.Kind = EcValueKind.RawByte;
        if (!Enum.IsDefined(typeof(SensorQuantity), sensor.Quantity)) sensor.Quantity = SensorQuantity.Fan;
        if (!float.IsFinite(sensor.Scale)) sensor.Scale = 1f;
        if (!float.IsFinite(sensor.Offset)) sensor.Offset = 0f;
        if (!float.IsFinite(sensor.Divisor) || sensor.Divisor <= 0f) sensor.Divisor = 1000000f;
    }

    private static string MakeUniqueProfileName(string? candidate, int ordinal, HashSet<string> names)
    {
        string baseName = string.IsNullOrWhiteSpace(candidate) ? "Profile " + ordinal : candidate.Trim();
        string name = baseName;
        for (int suffix = 2; !names.Add(name); suffix++)
            name = baseName + " " + suffix;
        return name;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new PointJsonConverter());
        options.Converters.Add(new RectangleJsonConverter());
        return options;
    }

    // System.Text.Json would serialize Point/Rectangle via reflection, but Rectangle
    // carries redundant get-only members and the shape is fragile across runtimes.
    // Explicit converters pin the wire format to {x,y} / {x,y,w,h}.

    private static int ReadInt32(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            reader.Skip();
            return 0;
        }
        return reader.TryGetInt32(out int v) ? v : (int)reader.GetDouble();
    }

    private sealed class PointJsonConverter : JsonConverter<Point>
    {
        public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected object for Point.");

            int x = 0, y = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return new Point(x, y);
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("Malformed Point object.");

                string name = reader.GetString() ?? "";
                if (!reader.Read()) break;

                if (name.Equals("x", StringComparison.OrdinalIgnoreCase)) x = ReadInt32(ref reader);
                else if (name.Equals("y", StringComparison.OrdinalIgnoreCase)) y = ReadInt32(ref reader);
                else reader.Skip();
            }
            throw new JsonException("Unterminated Point object.");
        }

        public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteEndObject();
        }
    }

    private sealed class RectangleJsonConverter : JsonConverter<Rectangle>
    {
        public override Rectangle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected object for Rectangle.");

            int x = 0, y = 0, w = 0, h = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return new Rectangle(x, y, w, h);
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("Malformed Rectangle object.");

                string name = reader.GetString() ?? "";
                if (!reader.Read()) break;

                if (name.Equals("x", StringComparison.OrdinalIgnoreCase)) x = ReadInt32(ref reader);
                else if (name.Equals("y", StringComparison.OrdinalIgnoreCase)) y = ReadInt32(ref reader);
                else if (name.Equals("w", StringComparison.OrdinalIgnoreCase)
                      || name.Equals("width", StringComparison.OrdinalIgnoreCase)) w = ReadInt32(ref reader);
                else if (name.Equals("h", StringComparison.OrdinalIgnoreCase)
                      || name.Equals("height", StringComparison.OrdinalIgnoreCase)) h = ReadInt32(ref reader);
                else reader.Skip();
            }
            throw new JsonException("Unterminated Rectangle object.");
        }

        public override void Write(Utf8JsonWriter writer, Rectangle value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteNumber("w", value.Width);
            writer.WriteNumber("h", value.Height);
            writer.WriteEndObject();
        }
    }
}
