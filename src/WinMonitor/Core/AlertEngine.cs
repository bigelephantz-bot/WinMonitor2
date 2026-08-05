using WinMonitor.Config;

namespace WinMonitor.Core;

/// <summary>Raised when a sensor stayed at/above its red threshold for the sustain time.</summary>
public sealed record AlertEvent(
    string SensorId,
    string DisplayName,
    float Value,
    float Threshold,
    bool PlaySound,
    string? SoundPath);

/// <summary>
/// Threshold + sustain alert filter. Per-sensor state machine:
/// Idle → OverRed(since) → Alerted. A value ≥ red starts the timer; it must remain ≥ red
/// continuously for SustainSeconds (any tick below red — or a missing sample — resets to
/// Idle). After raising, the sensor re-arms when the value falls below yellow OR after a
/// 10-minute cooldown. Accept runs on the polling background thread; ReloadConfig on the
/// UI thread — one lock guards all state.
/// </summary>
public sealed class AlertEngine
{
    private static readonly TimeSpan RearmCooldown = TimeSpan.FromMinutes(10);

    private enum Phase
    {
        Idle,
        OverRed,
        Alerted,
    }

    private sealed class SensorState
    {
        public SensorDescriptor Descriptor = null!;
        public Thresholds Thresholds = null!;
        public Phase Phase;
        public DateTime OverRedSinceUtc;
        public DateTime AlertedAtUtc;
    }

    private readonly Func<AppConfig> _configProvider;
    private readonly object _gate = new();

    // Only sensors whose resolved thresholds have AlertEnabled. Rebuilt when the descriptor
    // list reference changes (hardware rescan) or after ReloadConfig.
    private readonly Dictionary<string, SensorState> _tracked = new(StringComparer.Ordinal);
    private IReadOnlyList<SensorDescriptor>? _cachedDescriptors;

    public event Action<AlertEvent>? AlertRaised;

    public AlertEngine(AppConfig config) : this(() => config)
    {
    }

    /// <summary>Uses the latest atomically replaced configuration snapshot during a rebuild.</summary>
    public AlertEngine(Func<AppConfig> configProvider)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <summary>Called on the polling background thread every tick with the full descriptor list.</summary>
    public void Accept(SensorSnapshot[] snapshots, IReadOnlyList<SensorDescriptor> descriptors)
    {
        List<AlertEvent>? pending = null;

        lock (_gate)
        {
            // Descriptor list is stable between rescans, so a reference check makes the
            // rebuild (and its per-sensor threshold resolution, which allocates) rare.
            if (!ReferenceEquals(descriptors, _cachedDescriptors))
                RebuildTracked(descriptors);

            for (int i = 0; i < snapshots.Length; i++)
            {
                var snap = snapshots[i];
                if (!_tracked.TryGetValue(snap.Id, out var state)) continue;
                Step(state, in snap, ref pending);
            }
        }

        // Raise outside the lock; the handler marshals to the UI thread itself.
        if (pending is not null)
        {
            for (int i = 0; i < pending.Count; i++)
                AlertRaised?.Invoke(pending[i]);
        }
    }

    /// <summary>Call after profile/threshold edits: drops all state and re-resolves thresholds lazily.</summary>
    public void ReloadConfig()
    {
        lock (_gate)
        {
            _tracked.Clear();
            _cachedDescriptors = null; // force RebuildTracked on the next Accept
        }
    }

    private static void Step(SensorState s, in SensorSnapshot snap, ref List<AlertEvent>? pending)
    {
        var t = s.Thresholds;
        var now = snap.UtcTimestamp;

        if (!snap.HasValue)
        {
            // Missing sample: we cannot prove the value stayed over red, so the sustain
            // timer restarts. An active alert just keeps waiting for its cooldown.
            if (s.Phase == Phase.OverRed) s.Phase = Phase.Idle;
            else if (s.Phase == Phase.Alerted && now - s.AlertedAtUtc >= RearmCooldown) s.Phase = Phase.Idle;
            return;
        }

        float v = snap.Value.GetValueOrDefault();

        if (s.Phase == Phase.Alerted)
        {
            if (v < t.Yellow || now - s.AlertedAtUtc >= RearmCooldown)
                s.Phase = Phase.Idle; // re-armed; re-evaluate this same tick below
            else
                return;
        }

        if (s.Phase == Phase.Idle)
        {
            if (v < t.Red) return;
            s.Phase = Phase.OverRed;
            s.OverRedSinceUtc = now;
            // fall through: SustainSeconds == 0 alerts on the same tick
        }

        // Phase.OverRed
        if (v < t.Red)
        {
            s.Phase = Phase.Idle;
            return;
        }

        if ((now - s.OverRedSinceUtc).TotalSeconds >= t.SustainSeconds)
        {
            s.Phase = Phase.Alerted;
            s.AlertedAtUtc = now;
            string name = string.IsNullOrWhiteSpace(s.Descriptor.DisplayName) ? s.Descriptor.Name : s.Descriptor.DisplayName;
            pending ??= new List<AlertEvent>(2);
            pending.Add(new AlertEvent(s.Descriptor.Id, name, v, t.Red, t.PlaySound, t.SoundPath));
        }
    }

    private void RebuildTracked(IReadOnlyList<SensorDescriptor> descriptors)
    {
        AppConfig config = _configProvider();
        // Preserve running state machines across a hardware rescan so sustain timers and
        // cooldowns survive for sensors that still exist.
        Dictionary<string, SensorState>? previous = null;
        if (_tracked.Count > 0)
            previous = new Dictionary<string, SensorState>(_tracked, StringComparer.Ordinal);
        _tracked.Clear();

        try
        {
            for (int i = 0; i < descriptors.Count; i++)
            {
                var d = descriptors[i];
                var thresholds = config.ResolveThresholds(d);
                if (!thresholds.AlertEnabled) continue;

                SensorState state;
                if (previous is not null && previous.TryGetValue(d.Id, out var old)) state = old;
                else state = new SensorState();

                state.Descriptor = d;
                state.Thresholds = thresholds;
                _tracked[d.Id] = state;
            }

            // Marked only after the whole rebuild succeeds; a mid-rebuild failure must not
            // pin a half-built _tracked as current.
            _cachedDescriptors = descriptors;
        }
        catch
        {
            // Drop the partial rebuild; the null marker makes the next Accept retry.
            _tracked.Clear();
            _cachedDescriptors = null;
        }
    }
}
