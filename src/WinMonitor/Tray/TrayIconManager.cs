using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;
using WinMonitor.UI;

namespace WinMonitor.Tray;

/// <summary>
/// Owns every NotifyIcon of the active profile. Threading model: <see cref="Accept"/> is
/// called on the sensor background thread and only writes into a ConcurrentDictionary plus
/// schedules ONE coalesced BeginInvoke; every NotifyIcon/GDI operation happens on the UI
/// thread anchored by the ISynchronizeInvoke passed to the constructor (SyncWindow).
/// Icons are re-rendered only when their text/color/style actually changed, and every
/// replaced HICON is destroyed through IconRenderer.ReleaseIcon.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private sealed class IconSlot
    {
        public NotifyIcon Icon = null!;
        public TrayIconConfig Cfg = null!;
        public int RotateIndex;
        public string LastText = "";
        public Color LastColor;
        public TrayIconStyle LastStyle;
        public string LastTooltip = "";
        public System.Drawing.Icon? CurrentIcon;              // released on replace/dispose
        public System.Windows.Forms.Timer? RotateTimer;
        public Color? OverrideColor;                          // parsed once per Rebuild
        public string? AutoSensorId;                          // cached auto-pick, reset on Rebuild
        public string[]? AutoIdList;                          // cached 1-element id list for FlyoutRequested (no per-click alloc)
        public string? TipSensorId;                           // raw inputs of the last-built tooltip;
        public float TipValue = float.NaN;                    // NaN encodes "no value"/"no stats"
        public float TipMin = float.NaN;
        public float TipMax = float.NaN;

        // Sparkline state (only touched when Cfg.ShowSparkline). SparkBuffer is reused per
        // tick so history sampling never allocates; SparkCount is how many leading entries
        // are valid. LastSparkHash is a hash of the whole drawn window for change-detection
        // (redraw when any point in the window moved, incl. an old spike scrolling off).
        // SparkDrawn records whether a line was actually drawn last time.
        public float[] SparkBuffer = System.Array.Empty<float>();
        public int SparkCount;
        public int LastSparkHash;
        public bool SparkDrawn;
    }

    private const int BalloonTimeoutMs = 5000;
    private const int MaxTooltipLength = 63;                  // hard shell limit for NotifyIcon.Text
    private const int SparklineSamples = 32;                  // last-N history points drawn in the tray sparkline

    // State colors come from Theme.Good/Warn/Hot so the tray matches the rest of the app
    // (the halo drawn by IconRenderer keeps them readable on light and dark taskbars).
    // Only the "no data" neutrals stay local.
    private static readonly Color NeutralGray = Color.FromArgb(170, 170, 170);
    private static readonly Color NeutralBadge = Color.FromArgb(96, 96, 96);

    private readonly Func<AppConfig> _configProvider;
    private readonly StatsTracker _stats;
    private readonly ISynchronizeInvoke _sync;
    private readonly List<IconSlot> _slots = new();
    private readonly ConcurrentDictionary<string, float?> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Thresholds> _thresholdCache = new(StringComparer.Ordinal); // UI thread only; cleared on Rebuild
    private readonly StringBuilder _tooltipBuilder = new(80); // UI thread only
    private readonly Action _redrawMarshaledAction;           // cached: no delegate alloc per tick

    private readonly ContextMenuStrip _menu;                  // ONE shared menu for all icons
    private readonly Font _menuBoldFont;
    private readonly ToolStripMenuItem _miOpen;
    private readonly ToolStripMenuItem _miCompact;
    private readonly ToolStripMenuItem _miSettings;
    private readonly ToolStripMenuItem _miResetPeaks;
    private readonly ToolStripMenuItem _miOpenLogs;
    private readonly ToolStripMenuItem _miTaskManager;
    private readonly ToolStripMenuItem _miExit;

    private IReadOnlyList<SensorDescriptor>? _descriptors;
    private NotifyIcon? _fallbackBalloon;                     // balloon anchor when no icons configured
    private int _redrawPending;                               // Interlocked coalescing flag
    private volatile bool _disposed;

    // Single/double-click disambiguation: a left MouseUp defers the flyout by the system
    // double-click time; a DoubleClick (open main window) cancels the pending timer so a
    // double-click never also pops the flyout. One shared reusable one-shot timer.
    private readonly System.Windows.Forms.Timer _clickTimer;
    private IReadOnlyList<string>? _pendingFlyoutIds;
    private Point _pendingFlyoutPos;

    public event Action? OpenMainRequested;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;
    public event Action? ResetPeaksRequested;
    public event Action? CompactModeRequested;
    /// <summary>Left-click on a tray icon: (sensor ids that icon currently shows, cursor position).</summary>
    public event Action<IReadOnlyList<string>, Point>? FlyoutRequested;

    public TrayIconManager(AppConfig config, StatsTracker stats, ISynchronizeInvoke sync)
        : this(() => config, stats, sync)
    {
    }

    /// <summary>Uses the latest fully replaced configuration whenever tray UI is rebuilt.</summary>
    public TrayIconManager(Func<AppConfig> configProvider, StatsTracker stats, ISynchronizeInvoke sync)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _stats = stats;
        _sync = sync;
        _redrawMarshaledAction = OnRedrawMarshaled;

        _clickTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, SystemInformation.DoubleClickTime) };
        _clickTimer.Tick += OnClickTimerTick;

        _menu = new ContextMenuStrip();
        _menuBoldFont = new Font(_menu.Font, FontStyle.Bold);

        _miOpen = new ToolStripMenuItem { Font = _menuBoldFont };   // bold = default action (double-click)
        _miOpen.Click += (_, _) => OpenMainRequested?.Invoke();
        _miCompact = new ToolStripMenuItem();
        _miCompact.Click += (_, _) => CompactModeRequested?.Invoke();
        _miSettings = new ToolStripMenuItem();
        _miSettings.Click += (_, _) => OpenSettingsRequested?.Invoke();
        _miResetPeaks = new ToolStripMenuItem();
        _miResetPeaks.Click += (_, _) => ResetPeaksRequested?.Invoke();
        _miOpenLogs = new ToolStripMenuItem();
        _miOpenLogs.Click += (_, _) => OpenLogFolder();
        _miTaskManager = new ToolStripMenuItem();
        _miTaskManager.Click += (_, _) => LaunchTaskManager();
        _miExit = new ToolStripMenuItem();
        _miExit.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(_miOpen);
        _menu.Items.Add(_miCompact);
        _menu.Items.Add(_miSettings);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_miResetPeaks);
        _menu.Items.Add(_miOpenLogs);
        _menu.Items.Add(_miTaskManager);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_miExit);

        RefreshMenuText();
    }

    // ---------- background-thread entry point ----------

    /// <summary>Bg thread. Stores latest values and coalesces at most one pending UI redraw.</summary>
    public void Accept(SensorSnapshot[] snapshots)
    {
        if (_disposed) return;

        bool changed = false;
        for (int i = 0; i < snapshots.Length; i++)
        {
            var s = snapshots[i];
            float? next = s.HasValue ? s.Value : null;
            if (_latest.TryGetValue(s.Id, out var prev) && FloatsEqual(prev, next)) continue;
            _latest[s.Id] = next;
            changed = true;
        }
        // Nothing new (first-time entries count as changes): rotation timers redraw on
        // their own schedule, so skipping the marshal here keeps idle ticks free.
        if (!changed) return;

        // If a redraw is already queued for the UI thread, skip: it will pick up the
        // values we just stored. Prevents BeginInvoke pile-up when the UI thread stalls.
        if (Interlocked.CompareExchange(ref _redrawPending, 1, 0) != 0) return;
        try
        {
            _sync.BeginInvoke(_redrawMarshaledAction, null);
        }
        catch (Exception)
        {
            // Sync window torn down during shutdown; re-arm so a later tick can retry.
            Interlocked.Exchange(ref _redrawPending, 0);
        }
    }

    private void OnRedrawMarshaled()
    {
        // Clear the flag BEFORE redrawing so a bg tick arriving mid-redraw queues the next one.
        Interlocked.Exchange(ref _redrawPending, 0);
        if (_disposed) return;
        RedrawAll();
    }

    // ---------- rebuild ----------

    public void Rebuild(IReadOnlyList<SensorDescriptor> descriptors)
    {
        if (_disposed) return;
        if (_sync.InvokeRequired)
        {
            try { _sync.BeginInvoke(new Action<IReadOnlyList<SensorDescriptor>>(RebuildCore), new object[] { descriptors }); }
            catch (Exception) { /* shutting down */ }
            return;
        }
        RebuildCore(descriptors);
    }

    private void RebuildCore(IReadOnlyList<SensorDescriptor> descriptors)
    {
        if (_disposed) return;
        _descriptors = descriptors;
        _thresholdCache.Clear();
        RefreshMenuText();   // picks up language changes applied via settings

        for (int i = 0; i < _slots.Count; i++) DisposeSlot(_slots[i]);
        _slots.Clear();

        var configs = _configProvider().Active.TrayIcons;
        for (int i = 0; i < configs.Count; i++)
        {
            var cfg = configs[i];
            var slot = new IconSlot { Cfg = cfg };

            if (!string.IsNullOrWhiteSpace(cfg.ColorOverride))
            {
                try { slot.OverrideColor = ColorTranslator.FromHtml(cfg.ColorOverride); }
                catch (Exception) { slot.OverrideColor = null; }   // bad HTML color → threshold colors
            }

            var notify = new NotifyIcon
            {
                ContextMenuStrip = _menu,
                Text = Loc.T("app.name"),
            };
            notify.DoubleClick += OnIconDoubleClick;
            var clickSlot = slot;   // flyout on left-click; never wired on the balloon-only fallback icon
            notify.MouseUp += (_, e) => OnIconMouseUp(clickSlot, e);
            slot.Icon = notify;
            slot.LastTooltip = Loc.T("app.name");

            // Neutral "…" placeholder until the first snapshot arrives.
            try
            {
                Color fg = cfg.Style == TrayIconStyle.TextOnBadge ? Color.White : NeutralGray;
                Color bg = cfg.Style == TrayIconStyle.TextOnBadge ? NeutralBadge : Color.Transparent;
                slot.CurrentIcon = IconRenderer.RenderText("…", fg, bg, cfg.Bold);
                notify.Icon = slot.CurrentIcon;
                slot.LastText = "…";
                slot.LastColor = NeutralGray;
                slot.LastStyle = cfg.Style;
            }
            catch (Exception) { /* icon stays null; first successful redraw sets it */ }

            if (cfg.SensorIds.Count > 1)
            {
                var timer = new System.Windows.Forms.Timer
                {
                    Interval = Math.Max(1, cfg.RotateIntervalSec) * 1000,
                };
                var captured = slot;
                timer.Tick += (_, _) => OnRotateTick(captured);
                slot.RotateTimer = timer;
                timer.Start();
            }

            notify.Visible = true;
            _slots.Add(slot);
        }

        // Real icons exist again: the balloon-only fallback is no longer needed.
        if (_slots.Count > 0 && _fallbackBalloon is not null)
        {
            try { _fallbackBalloon.Visible = false; } catch (Exception) { }
            _fallbackBalloon.Dispose();
            _fallbackBalloon = null;
        }

        RedrawAll();
    }

    private void OnIconDoubleClick(object? sender, EventArgs e)
    {
        // Cancel the pending single-click flyout BEFORE opening the main window so a
        // double-click opens the window without also popping the flyout.
        _clickTimer.Stop();
        _pendingFlyoutIds = null;
        OpenMainRequested?.Invoke();
    }

    /// <summary>
    /// Left-click requests the flyout for the sensors this icon shows. Only real per-config
    /// icons get this handler, so a click on the balloon-only fallback icon never raises it.
    /// </summary>
    private void OnIconMouseUp(IconSlot slot, MouseEventArgs e)
    {
        if (_disposed || e.Button != MouseButtons.Left) return;
        var ids = ResolveFlyoutIds(slot);
        if (ids is null || ids.Count == 0) return;
        // Defer the flyout by the double-click window; a DoubleClick cancels it. Capture the
        // ids and cursor position now — by tick time the cursor may have moved.
        _pendingFlyoutIds = ids;
        _pendingFlyoutPos = Cursor.Position;
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void OnClickTimerTick(object? sender, EventArgs e)
    {
        _clickTimer.Stop();   // one-shot
        if (_disposed) return;
        var ids = _pendingFlyoutIds;
        _pendingFlyoutIds = null;
        if (ids is not null && ids.Count > 0)
            FlyoutRequested?.Invoke(ids, _pendingFlyoutPos);
    }

    /// <summary>
    /// Sensor ids the flyout should list for this icon. Explicit configs expose their whole
    /// list (the carousel shows them all); auto mode wraps the resolved auto id in a
    /// one-element array cached on the slot so a click never allocates.
    /// </summary>
    private IReadOnlyList<string>? ResolveFlyoutIds(IconSlot slot)
    {
        if (slot.Cfg.SensorIds.Count > 0) return slot.Cfg.SensorIds;
        slot.AutoSensorId ??= PickAutoSensor();
        string? auto = slot.AutoSensorId;
        if (auto is null) return null;
        if (slot.AutoIdList is null || !string.Equals(slot.AutoIdList[0], auto, StringComparison.Ordinal))
            slot.AutoIdList = new[] { auto };
        return slot.AutoIdList;
    }

    private void OnRotateTick(IconSlot slot)
    {
        if (_disposed) return;
        int count = slot.Cfg.SensorIds.Count;
        if (count > 1)
        {
            slot.RotateIndex = (slot.RotateIndex + 1) % count;
            RedrawSlot(slot);
        }
    }

    // ---------- redraw (UI thread only) ----------

    private void RedrawAll()
    {
        if (_disposed) return;
        for (int i = 0; i < _slots.Count; i++)
            RedrawSlot(_slots[i]);
    }

    private void RedrawSlot(IconSlot slot)
    {
        if (_disposed || _latest.IsEmpty) return;   // keep "…" until the first data tick

        string? id = ResolveSensorId(slot);
        SensorDescriptor? descriptor = id is null ? null : FindDescriptor(id);
        float? value = null;
        if (id is not null && _latest.TryGetValue(id, out var latest) && latest is { } v && !float.IsNaN(v))
            value = v;

        string text = FormatShort(descriptor, value);
        Color state = ResolveStateColor(slot, descriptor, value);
        TrayIconStyle style = slot.Cfg.Style;

        // Refresh the sparkline sample buffer (no-op / cleared when the feature is off or the
        // sensor has too little history). wantSpark drives both change-detection and the
        // render-path choice below.
        bool wantSpark = false;
        int sparkHash = 0;
        if (slot.Cfg.ShowSparkline)
        {
            SampleSparkline(slot, id);
            wantSpark = slot.SparkCount >= 2;
            if (wantSpark) sparkHash = SparkHash(slot.SparkBuffer, slot.SparkCount);
        }
        else if (slot.SparkCount != 0)
        {
            slot.SparkCount = 0;   // feature toggled off under us: forget any cached samples
        }

        // Redraw when the number, color, style, sparkline presence, or the drawn sparkline WINDOW
        // changed. Hashing the whole window (not just the newest sample) means an old spike
        // scrolling out of the 32-sample window still triggers the redraw that clears it.
        if (!string.Equals(text, slot.LastText, StringComparison.Ordinal)
            || state.ToArgb() != slot.LastColor.ToArgb()
            || style != slot.LastStyle
            || wantSpark != slot.SparkDrawn
            || (wantSpark && sparkHash != slot.LastSparkHash))
        {
            Color fg, bg;
            if (style == TrayIconStyle.TextOnBadge)
            {
                fg = Color.White;
                // A white badge would hide the white digits (Level sensors are "always white").
                bg = state.ToArgb() == Color.White.ToArgb() ? NeutralBadge : state;
            }
            else
            {
                fg = state;
                bg = Color.Transparent;
            }

            // Sparkline line color reuses the resolved threshold/override color, matching the
            // number so the icon reads as one unit. On a badge the number is white while the
            // graph keeps the state color for a legible contrast against the badge fill.
            Color lineColor = style == TrayIconStyle.TextOnBadge && bg.ToArgb() != state.ToArgb()
                ? Color.White
                : state;

            try
            {
                var next = wantSpark
                    ? IconRenderer.RenderTextWithSparkline(text, fg, bg, slot.Cfg.Bold,
                        new ReadOnlySpan<float>(slot.SparkBuffer, 0, slot.SparkCount), lineColor)
                    : IconRenderer.RenderText(text, fg, bg, slot.Cfg.Bold);
                var previous = slot.CurrentIcon;
                slot.Icon.Icon = next;   // the shell copies the icon; the old HICON can go now
                slot.CurrentIcon = next;
                if (previous is not null) IconRenderer.ReleaseIcon(previous);
                slot.LastText = text;
                slot.LastColor = state;
                slot.LastStyle = style;
                slot.LastSparkHash = sparkHash;
                slot.SparkDrawn = wantSpark;
            }
            catch (Exception)
            {
                // GDI pressure or shell hiccup: keep showing the previous icon.
            }
        }

        UpdateTooltip(slot, descriptor, value);
    }

    /// <summary>
    /// Fills <see cref="IconSlot.SparkBuffer"/> with the last <see cref="SparklineSamples"/>
    /// history values for <paramref name="id"/> (oldest→newest) and sets
    /// <see cref="IconSlot.SparkCount"/>. The buffer is allocated once per slot and reused, so
    /// this only touches an index-based copy — no per-tick List/LINQ allocation. The
    /// <see cref="StatsTracker.GetHistory"/> snapshot itself is copied on its side; we read it
    /// via the indexer without an enumerator.
    /// </summary>
    private void SampleSparkline(IconSlot slot, string? id)
    {
        if (id is null)
        {
            slot.SparkCount = 0;
            return;
        }

        if (slot.SparkBuffer.Length < SparklineSamples)
            slot.SparkBuffer = new float[SparklineSamples];

        // Copies only the last N values into our reusable buffer — no full-history array alloc.
        int take = _stats.CopyRecentHistory(id, slot.SparkBuffer, SparklineSamples);
        slot.SparkCount = take < 2 ? 0 : take;   // <2 points: nothing to draw
    }

    /// <summary>Order-sensitive hash of the drawn sparkline window, for change detection.</summary>
    private static int SparkHash(float[] buffer, int count)
    {
        int h = 17 + count;
        for (int i = 0; i < count; i++)
            h = h * 31 + buffer[i].GetHashCode();
        return h;
    }

    private string? ResolveSensorId(IconSlot slot)
    {
        var ids = slot.Cfg.SensorIds;
        if (ids.Count > 0)
        {
            int index = slot.RotateIndex;
            if ((uint)index >= (uint)ids.Count) index = 0;   // config edited under us
            return ids[index];
        }
        // Auto mode: resolve once per Rebuild so the icon doesn't hop between sensors.
        slot.AutoSensorId ??= PickAutoSensor();
        return slot.AutoSensorId;
    }

    private string? PickAutoSensor()
    {
        var list = _descriptors;
        return list is null ? null : SensorPicker.PickAuto(list);
    }

    private SensorDescriptor? FindDescriptor(string id)
    {
        var list = _descriptors;
        if (list is null) return null;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Id, id, StringComparison.Ordinal)) return list[i];
        return null;
    }

    private static bool SameFloat(float a, float b)
        => a == b || (float.IsNaN(a) && float.IsNaN(b));

    private static bool FloatsEqual(float? a, float? b)
        => a is { } av ? b is { } bv && SameFloat(av, bv) : b is null;

    /// <summary>
    /// Renders the value for a tray glyph: DIGITS ONLY, never a unit.
    ///
    /// A tray icon is 16 px at 100 % DPI. Every glyph spent on a unit is taken from the number,
    /// and past three glyphs adjacent stems merge into an unreadable blob — so the icon shows the
    /// bare reading at the largest size that fits, and the unit lives in the tooltip and the main
    /// window, which have room to be unambiguous.
    ///
    /// Magnitudes are folded into the number instead of being spelled out, so no scale
    /// information is lost: fan RPM is shown in hundreds ("34" = 3400 rpm) and frequency in GHz
    /// ("4.2" = 4200 MHz). Both are stated exactly in the tooltip.
    /// </summary>
    private static string FormatShort(SensorDescriptor? descriptor, float? value)
    {
        if (descriptor is null || value is not { } v) return "—";
        // Synthetic throttle sensor: language-neutral state word instead of a number.
        if (string.Equals(descriptor.Id, WellKnown.ThrottleSensorId, StringComparison.Ordinal))
            return v >= 0.5f ? "HOT" : "OK";
        switch (descriptor.Quantity)
        {
            case SensorQuantity.Temperature:
                return Units.FormatTempShort(v);

            case SensorQuantity.Fan:
                // Hundreds of RPM keeps a four-digit reading inside two glyphs ("34" = 3400).
                // Below 1000 rpm the raw value is already short enough to show as-is.
                return v >= 1000f
                    ? ((int)MathF.Round(v / 100f)).ToString(CultureInfo.InvariantCulture)
                    : ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture);

            case SensorQuantity.Level:
            case SensorQuantity.Load:
            case SensorQuantity.Control:
            case SensorQuantity.Power:
                return ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture);

            case SensorQuantity.Frequency:
                // Frequencies arrive in MHz; GHz with one decimal fits three glyphs.
                return v >= 1000f
                    ? (v / 1000f).ToString("0.0", CultureInfo.InvariantCulture)
                    : MathF.Round(v).ToString(CultureInfo.InvariantCulture);

            case SensorQuantity.Voltage:
                return v.ToString("0.#", CultureInfo.InvariantCulture);

            case SensorQuantity.Data:
                // Switch to TB at 1000 GB, not 1024: the 1000-1023 GB band would otherwise need
                // four digits, one more than the canvas can render legibly.
                return v >= 1000f
                    ? (v / 1024f).ToString("0.#", CultureInfo.InvariantCulture)
                    : ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture);

            default:
                return v.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    private Color ResolveStateColor(IconSlot slot, SensorDescriptor? descriptor, float? value)
    {
        if (slot.OverrideColor is { } fixedColor) return fixedColor;
        if (descriptor is null || value is not { } v) return NeutralGray;
        // Throttle state is binary — good/hot, no threshold resolution (its Level quantity
        // would otherwise land in the never-colored branch below).
        if (string.Equals(descriptor.Id, WellKnown.ThrottleSensorId, StringComparison.Ordinal))
            return v >= 0.5f ? Theme.Hot : Theme.Good;
        if (descriptor.Quantity == SensorQuantity.Level) return Color.White;   // bands are 101: never colored
        var thresholds = GetThresholds(descriptor);
        if (v >= thresholds.Red) return Theme.Hot;
        if (v >= thresholds.Yellow) return Theme.Warn;
        return Theme.Good;
    }

    private Thresholds GetThresholds(SensorDescriptor descriptor)
    {
        // ResolveThresholds allocates when falling back to SuggestFor; cache per Rebuild.
        if (_thresholdCache.TryGetValue(descriptor.Id, out var cached)) return cached;
        var resolved = _configProvider().ResolveThresholds(descriptor);
        _thresholdCache[descriptor.Id] = resolved;
        return resolved;
    }

    private void UpdateTooltip(IconSlot slot, SensorDescriptor? descriptor, float? value)
    {
        string tip;
        if (descriptor is null)
        {
            // TipSensorId null means the placeholder tooltip (set at slot creation or below)
            // is already showing.
            if (slot.TipSensorId is null) return;
            slot.TipSensorId = null;
            slot.TipValue = slot.TipMin = slot.TipMax = float.NaN;
            tip = Loc.T("app.name");
        }
        else
        {
            bool isThrottle = string.Equals(descriptor.Id, WellKnown.ThrottleSensorId, StringComparison.Ordinal);
            float v = value ?? float.NaN;   // RedrawSlot already mapped NaN readings to null
            float min = float.NaN, max = float.NaN;
            if (!isThrottle)   // binary min/max says nothing; the current state is the whole story
            {
                var stats = _stats.GetStats(descriptor.Id);
                if (stats is { HasData: true })
                {
                    min = stats.Min;
                    max = stats.Max;
                }
            }

            // Same raw inputs as last time: the tooltip text is identical, skip all formatting.
            if (string.Equals(descriptor.Id, slot.TipSensorId, StringComparison.Ordinal)
                && SameFloat(v, slot.TipValue) && SameFloat(min, slot.TipMin) && SameFloat(max, slot.TipMax))
                return;
            slot.TipSensorId = descriptor.Id;
            slot.TipValue = v;
            slot.TipMin = min;
            slot.TipMax = max;

            string val = isThrottle
                ? (float.IsNaN(v) ? "—" : Loc.T(v >= 0.5f ? "boolean.true" : "boolean.false"))
                : Units.Format(descriptor.Quantity, value);
            string range = "";
            if (!float.IsNaN(min))
                range = "\n" + Units.Format(descriptor.Quantity, min) + " / " + Units.Format(descriptor.Quantity, max);

            string name = descriptor.DisplayName.Length > 0 ? descriptor.DisplayName : descriptor.Name;
            int budget = MaxTooltipLength - val.Length - 1 - range.Length;
            if (budget < 0)
            {
                range = "";
                budget = MaxTooltipLength - val.Length - 1;
            }
            if (name.Length > budget)
                name = budget > 1 ? string.Concat(name.AsSpan(0, budget - 1), "…") : "";

            _tooltipBuilder.Clear();
            _tooltipBuilder.Append(name).Append(' ').Append(val).Append(range);
            if (_tooltipBuilder.Length > MaxTooltipLength) _tooltipBuilder.Length = MaxTooltipLength;
            tip = _tooltipBuilder.ToString();
        }

        // Setting NotifyIcon.Text re-registers the icon and flickers an open tooltip: only on change.
        if (string.Equals(tip, slot.LastTooltip, StringComparison.Ordinal)) return;
        slot.LastTooltip = tip;
        try { slot.Icon.Text = tip; }
        catch (ArgumentException) { /* defensive length guard; never crash the tray */ }
    }

    // ---------- balloons ----------

    public void ShowToast(string title, string message, ToolTipIcon icon)
    {
        if (_disposed) return;
        if (_sync.InvokeRequired)
        {
            try { _sync.BeginInvoke(new Action<string, string, ToolTipIcon>(ShowToastCore), new object[] { title, message, icon }); }
            catch (Exception) { }
            return;
        }
        ShowToastCore(title, message, icon);
    }

    private void ShowToastCore(string title, string message, ToolTipIcon icon)
    {
        if (_disposed || string.IsNullOrEmpty(message)) return;
        try
        {
            if (_slots.Count > 0)
            {
                _slots[0].Icon.ShowBalloonTip(BalloonTimeoutMs, title, message, icon);
                return;
            }
            // No tray icons configured: keep one icon purely as a balloon anchor.
            // The shell only shows balloons for icons that are actually added (Visible = true).
            _fallbackBalloon ??= new NotifyIcon
            {
                Icon = SystemIcons.Application,   // shared system icon: never dispose/destroy it
                Text = Loc.T("app.name"),
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _fallbackBalloon.ShowBalloonTip(BalloonTimeoutMs, title, message, icon);
        }
        catch (Exception) { /* balloon failures must never break monitoring */ }
    }

    // ---------- taskbar restart ----------

    /// <summary>
    /// After an Explorer crash the shell forgot our icons and a Visible=false/true dance on
    /// the old NotifyIcons is unreliable — recreating them via a full Rebuild is the robust
    /// path. Called from SyncWindow.WndProc (UI thread) on the TaskbarCreated broadcast.
    /// </summary>
    public void RefreshAfterTaskbarRestart()
    {
        if (_disposed) return;
        if (_fallbackBalloon is not null)
        {
            try { _fallbackBalloon.Dispose(); } catch (Exception) { }
            _fallbackBalloon = null;   // recreated lazily by the next ShowToast
        }
        Rebuild(_descriptors ?? Array.Empty<SensorDescriptor>());
    }

    // ---------- menu actions ----------

    private void RefreshMenuText()
    {
        _miOpen.Text = Loc.T("tray.open");
        _miCompact.Text = Loc.T("tray.compact");
        _miSettings.Text = Loc.T("tray.settings");
        _miResetPeaks.Text = Loc.T("tray.reset_peaks");
        _miOpenLogs.Text = Loc.T("tray.open_logs");
        _miTaskManager.Text = Loc.T("tray.task_manager");
        _miExit.Text = Loc.T("tray.exit");
    }

    private static void OpenLogFolder()
    {
        try
        {
            // Same directory HistoryLogger writes to (ConfigDirectory\logs).
            string dir = Path.Combine(ConfigStore.ConfigDirectory, "logs");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception) { /* explorer missing/blocked: nothing sensible to do */ }
    }

    private static void LaunchTaskManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception) { /* user may cancel the UAC prompt */ }
    }

    // ---------- disposal ----------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_sync.InvokeRequired)
        {
            try
            {
                _sync.Invoke(new Action(DisposeCore), null);
                return;
            }
            catch (Exception) { /* sync window already gone: best-effort direct cleanup */ }
        }
        DisposeCore();
    }

    private void DisposeCore()
    {
        for (int i = 0; i < _slots.Count; i++) DisposeSlot(_slots[i]);
        _slots.Clear();

        if (_fallbackBalloon is not null)
        {
            try { _fallbackBalloon.Visible = false; } catch (Exception) { }
            _fallbackBalloon.Dispose();
            _fallbackBalloon = null;
        }

        _clickTimer.Stop();
        _clickTimer.Dispose();
        _menu.Dispose();
        _menuBoldFont.Dispose();
    }

    private static void DisposeSlot(IconSlot slot)
    {
        if (slot.RotateTimer is not null)
        {
            slot.RotateTimer.Stop();
            slot.RotateTimer.Dispose();
            slot.RotateTimer = null;
        }
        try { slot.Icon.Visible = false; } catch (Exception) { }
        slot.Icon.Icon = null;    // detach from the shell wrapper before destroying the HICON
        slot.Icon.Dispose();
        if (slot.CurrentIcon is not null)
        {
            IconRenderer.ReleaseIcon(slot.CurrentIcon);
            slot.CurrentIcon = null;
        }
    }
}
