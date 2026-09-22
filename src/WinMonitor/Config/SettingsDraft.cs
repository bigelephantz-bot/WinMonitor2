using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinMonitor.Config;

/// <summary>
/// An isolated settings transaction. Draft edits never mutate live objects; applying constructs a
/// new merged configuration so the caller can persist it before publishing it to running services.
/// </summary>
public sealed class SettingsDraft
{
    private string _baselineJson;
    public AppConfig Draft { get; private set; }

    public SettingsDraft(AppConfig live)
    {
        _baselineJson = JsonSerializer.Serialize(live);
        Draft = Deserialize(_baselineJson);
    }

    public bool Rebase(AppConfig live)
    {
        string liveJson = JsonSerializer.Serialize(live);
        if (string.Equals(liveJson, _baselineJson, StringComparison.Ordinal)) return false;
        Draft = MergeDraftNode(JsonNode.Parse(_baselineJson), JsonSerializer.SerializeToNode(Draft),
            JsonNode.Parse(liveJson), null)?.Deserialize<AppConfig>() ?? Deserialize(liveJson);
        _baselineJson = liveJson;
        return true;
    }

    public AppConfig BuildMergedConfig(AppConfig live)
        => MergeDraftNode(JsonNode.Parse(_baselineJson), JsonSerializer.SerializeToNode(Draft),
            JsonSerializer.SerializeToNode(live), null)?.Deserialize<AppConfig>() ?? Deserialize(_baselineJson);

    public void Reset(AppConfig live)
    {
        _baselineJson = JsonSerializer.Serialize(live);
        Draft = Deserialize(_baselineJson);
    }

    public void RestoreDefaults() => Draft = new AppConfig();

    private static AppConfig Deserialize(string json)
        => JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

    internal static JsonNode? MergeDraftNode(JsonNode? baseline, JsonNode? draft, JsonNode? live, string? propertyName)
    {
        if (JsonNode.DeepEquals(draft, baseline)) return live?.DeepClone();
        if (JsonNode.DeepEquals(live, baseline)) return draft?.DeepClone();
        if (JsonNode.DeepEquals(draft, live)) return draft?.DeepClone();

        if (baseline is JsonObject baselineObject && draft is JsonObject draftObject && live is JsonObject liveObject)
        {
            var result = new JsonObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in baselineObject) names.Add(pair.Key);
            foreach (var pair in draftObject) names.Add(pair.Key);
            foreach (var pair in liveObject) names.Add(pair.Key);
            foreach (string name in names)
            {
                baselineObject.TryGetPropertyValue(name, out JsonNode? baselineValue);
                draftObject.TryGetPropertyValue(name, out JsonNode? draftValue);
                liveObject.TryGetPropertyValue(name, out JsonNode? liveValue);
                result[name] = MergeDraftNode(baselineValue, draftValue, liveValue, name);
            }
            return result;
        }

        if (propertyName == nameof(AppConfig.Profiles)
            && baseline is JsonArray baselineProfiles
            && draft is JsonArray draftProfiles
            && live is JsonArray liveProfiles)
        {
            return MergeProfiles(baselineProfiles, draftProfiles, liveProfiles);
        }

        if (propertyName == nameof(Profile.TrayIcons)
            && baseline is JsonArray baselineIcons
            && draft is JsonArray draftIcons
            && live is JsonArray liveIcons)
        {
            return MergeTrayIcons(baselineIcons, draftIcons, liveIcons);
        }

        // A collection with two concurrent edits has no general item identity. Prefer the draft:
        // applying a list edit must keep its selected order and explicit removals deterministic.
        return draft?.DeepClone();
    }

    /// <summary>
    /// Tray icons need their own rule because they are the one collection edited from two places at
    /// once: this dialog, and the main window's row context menu (which adds or removes a
    /// single-sensor icon on the live config without raising SettingsApplied). Preferring the draft
    /// wholesale means an icon toggled on in the main list vanishes the moment Apply is pressed.
    ///
    /// Identity is the ordered sensor-id set. <see cref="TrayIconConfig"/> carries no persistent id,
    /// and introducing one would mean a config schema bump for a merge edge case; the sensor set
    /// distinguishes every icon that can currently be created outside this dialog. If any of the
    /// three lists contains duplicate keys the set is genuinely ambiguous, so the draft wins as before.
    /// </summary>
    private static JsonArray MergeTrayIcons(JsonArray baseline, JsonArray draft, JsonArray live)
    {
        static string Key(JsonNode? node)
        {
            if (node is not JsonObject icon || icon["SensorIds"] is not JsonArray ids) return "";
            // Length-prefixed so an id containing the separator cannot forge another key.
            var sb = new StringBuilder();
            foreach (JsonNode? id in ids)
            {
                string value = id?.GetValue<string>() ?? "";
                sb.Append(value.Length).Append(':').Append(value).Append('|');
            }
            return sb.ToString();
        }

        static Dictionary<string, JsonObject>? IndexByKey(JsonArray array)
        {
            var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (JsonNode? node in array)
            {
                if (node is not JsonObject icon) continue;
                // Duplicates make the key useless as identity; fall back to the old behavior.
                if (!result.TryAdd(Key(icon), icon)) return null;
            }
            return result;
        }

        Dictionary<string, JsonObject>? baselineByKey = IndexByKey(baseline);
        Dictionary<string, JsonObject>? draftByKey = IndexByKey(draft);
        Dictionary<string, JsonObject>? liveByKey = IndexByKey(live);
        if (baselineByKey is null || draftByKey is null || liveByKey is null) return (JsonArray)draft.DeepClone();

        var merged = new JsonArray();
        foreach (JsonNode? node in draft)
        {
            if (node is not JsonObject draftIcon) continue;
            string key = Key(draftIcon);
            baselineByKey.TryGetValue(key, out JsonObject? baselineIcon);
            bool liveHasIt = liveByKey.TryGetValue(key, out JsonObject? liveIcon);

            // Removed live and untouched here (a row toggled off, or an obsolete sensor pruned):
            // that removal is the newer intent, so honour it instead of resurrecting the icon.
            if (baselineIcon is not null && !liveHasIt && JsonNode.DeepEquals(draftIcon, baselineIcon))
                continue;

            merged.Add(MergeDraftNode(baselineIcon, draftIcon, liveIcon ?? draftIcon,
                nameof(Profile.TrayIcons) + ".item") ?? draftIcon.DeepClone());
        }
        foreach (JsonNode? node in live)
        {
            if (node is not JsonObject liveIcon) continue;
            string key = Key(liveIcon);
            if (draftByKey.ContainsKey(key) || baselineByKey.ContainsKey(key)) continue;
            merged.Add(liveIcon.DeepClone());   // added outside this dialog while it was open
        }
        return merged;
    }

    private static JsonArray MergeProfiles(JsonArray baseline, JsonArray draft, JsonArray live)
    {
        static Dictionary<string, JsonObject> IndexByName(JsonArray array)
        {
            var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var node in array)
            {
                if (node is not JsonObject profile) continue;
                string? name = profile["Name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name)) result[name] = profile;
            }
            return result;
        }

        var baselineByName = IndexByName(baseline);
        var draftByName = IndexByName(draft);
        var liveByName = IndexByName(live);
        var result = new JsonArray();

        // The draft's ordering is intentional. Retain profiles independently added outside this
        // dialog after it, so a concurrent profile action does not vanish on Apply.
        foreach (var node in draft)
        {
            if (node is not JsonObject draftProfile) continue;
            string? name = draftProfile["Name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            baselineByName.TryGetValue(name, out var baselineProfile);
            liveByName.TryGetValue(name, out var liveProfile);
            result.Add(MergeDraftNode(baselineProfile, draftProfile, liveProfile, nameof(AppConfig.Profiles)));
        }
        foreach (var node in live)
        {
            if (node is not JsonObject liveProfile) continue;
            string? name = liveProfile["Name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || draftByName.ContainsKey(name)) continue;
            // A draft deletion of an existing profile is explicit; retain only genuinely new live profiles.
            if (!baselineByName.ContainsKey(name)) result.Add(liveProfile.DeepClone());
        }
        return result;
    }

}
