using System.Text.Json;
using WinMonitor.Config;

namespace WinMonitor.Core;

/// <summary>
/// UI-owned change detection for the complete persisted EC definition. Capturing the serialized
/// value keeps the baseline independent of subsequent in-place edits and includes future fields.
/// This is a cold settings path, never a polling operation.
/// </summary>
public sealed class EcSettingsPublisher
{
    private readonly Action<EcConfig> _publish;
    private string _applied;

    public EcSettingsPublisher(EcConfig initial, Action<EcConfig> publish)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _applied = JsonSerializer.Serialize(initial);
    }

    public bool Apply(EcConfig candidate)
    {
        string next = JsonSerializer.Serialize(candidate);
        if (string.Equals(next, _applied, StringComparison.Ordinal)) return false;
        // Update the baseline only after publication succeeds, allowing a failed apply to retry.
        _publish(candidate.Clone());
        _applied = next;
        return true;
    }
}
