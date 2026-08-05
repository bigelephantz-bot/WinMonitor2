using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.UI;

/// <summary>
/// Small borderless popup shown above the tray on a left-click: one row per sensor the
/// clicked icon represents (name + threshold-colored current value + dim session min/max).
/// Snapshots arrive on the polling thread and are coalesced onto the UI thread exactly like
/// CompactForm; painting only uses strings/colors cached per row, and text is reformatted
/// only when the underlying value actually changed. Auto-hides on deactivate, on Esc, or
/// after 8 seconds without interaction. Never steals activation when it appears.
/// </summary>
public sealed class FlyoutForm : Form
{
    private sealed class Row
    {
        public string Id = "";
        public string Name = "";
        public SensorQuantity Quantity;
        public Thresholds Thresholds = new();
        public bool IsThrottle;       // synthetic throttle sensor renders a state word, not a %
        public string ValueText = "—";
        public string RangeText = ""; // "min / max", empty when no stats (or throttle)
        public int ColorState = -1;   // -1 no data, 0 good, 1 warn, 2 hot
        public bool HasValue;
        public float LastValue = float.NaN;
        public float LastMin = float.NaN;   // NaN encodes "no stats yet"
        public float LastMax = float.NaN;
    }

    private const int Pad = 10;
    private const int Gap = 12;
    private const int HideAfterMs = 8000;
    private const int WsExToolWindow = 0x00000080;

    private readonly WinMonitorContext _ctx;
    private readonly Font _font = new("Segoe UI", 9.5f);
    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, Row> _rowById = new(StringComparer.Ordinal);
    private readonly List<string> _ids = new();          // ids of the last ShowFor call
    private readonly Action _applyPendingDelegate;
    private readonly System.Windows.Forms.Timer _hideTimer;

    // Written by the polling thread, drained on the UI thread; non-null means a UI
    // callback is already queued (coalescing — at most one BeginInvoke in flight).
    private SensorSnapshot[]? _pending;

    private string _emptyText = "—";
    private int _pad = Pad;   // DPI-scaled at layout time; baseline until the first RecalcLayout
    private int _gap = Gap;
    private int _rowHeight = 20;
    private int _nameColWidth = 60;
    private int _valueColWidth = 40;
    private int _rangeColWidth;

    public FlyoutForm(WinMonitorContext ctx)
    {
        _ctx = ctx;
        _applyPendingDelegate = ApplyPendingSnapshots;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Text = Loc.T("app.name");
        TopMost = true;
        ShowInTaskbar = false;
        Font = _font;

        _hideTimer = new System.Windows.Forms.Timer { Interval = HideAfterMs };
        _hideTimer.Tick += (_, _) => Hide();

        ReloadSensors();
    }

    /// <summary>The popup must never yank focus from whatever the user was doing.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow;   // keep the popup out of Alt-Tab
            return cp;
        }
    }

    // ---------- public contract ----------

    /// <summary>
    /// Rebuilds the rows for <paramref name="sensorIds"/> and shows the popup above
    /// <paramref name="screenPos"/>, clamped to that monitor's working area. UI thread only.
    /// </summary>
    public void ShowFor(IReadOnlyList<string> sensorIds, Point screenPos)
    {
        _ids.Clear();
        for (int i = 0; i < sensorIds.Count; i++) _ids.Add(sensorIds[i]);
        ReloadSensors();

        // Place the popup on the target monitor and show it (without stealing activation) so its
        // handle — and DeviceDpi — settle on that monitor. Then relayout at the real DPI and
        // re-clamp: on a mixed-DPI setup the first layout used the startup/previous monitor's DPI.
        Location = ClampAbove(screenPos);
        Show();

        RecalcLayout();
        Location = ClampAbove(screenPos);

        ResetHideTimer();
    }

    /// <summary>Position centered above <paramref name="screenPos"/>, clamped to that monitor's
    /// working area using the current <see cref="Control.Size"/>.</summary>
    private Point ClampAbove(Point screenPos)
    {
        var wa = Screen.FromPoint(screenPos).WorkingArea;
        int x = Math.Clamp(screenPos.X - Width / 2, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        int y = Math.Clamp(screenPos.Y - Height - 8, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        return new Point(x, y);
    }

    /// <summary>Called on the sensor polling thread; coalesces into one queued UI update.</summary>
    public void AcceptSnapshots(SensorSnapshot[] snapshots)
    {
        if (snapshots is null || IsDisposed || !IsHandleCreated) return;
        if (Interlocked.Exchange(ref _pending, snapshots) is not null) return; // update already queued
        try
        {
            BeginInvoke(_applyPendingDelegate);
        }
        catch (ObjectDisposedException) { Interlocked.Exchange(ref _pending, null); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _pending, null); }
    }

    /// <summary>
    /// Recomputes the row list from config + descriptors for the last shown sensor ids and
    /// re-reads theme/localized strings. UI thread only (also the SettingsApplied path).
    /// </summary>
    public void ReloadSensors()
    {
        _emptyText = Loc.T("common.none");
        BackColor = Theme.ChartBack;

        _rows.Clear();
        _rowById.Clear();
        var config = _ctx.Config;
        var descriptors = _ctx.Sensors.Descriptors;
        for (int i = 0; i < _ids.Count; i++)
        {
            var d = FindDescriptor(descriptors, _ids[i]);
            if (d is null) continue;   // sensor vanished or not present on this machine
            var row = new Row
            {
                Id = d.Id,
                Name = config.DisplayNameFor(d),
                Quantity = d.Quantity,
                Thresholds = config.ResolveThresholds(d),
                IsThrottle = string.Equals(d.Id, WellKnown.ThrottleSensorId, StringComparison.Ordinal),
            };
            // Pre-fill from the latest retained sample; the next poll can be seconds away.
            UpdateRowValue(row, _ctx.Stats.GetLatestValue(d.Id));
            UpdateRowRange(row);
            _rows.Add(row);
            _rowById[row.Id] = row;
        }

        RecalcLayout();
        Invalidate();
    }

    // ---------- data intake (UI thread) ----------

    private void ApplyPendingSnapshots()
    {
        var snaps = Interlocked.Exchange(ref _pending, null);
        if (snaps is null || IsDisposed || !Visible) return;

        bool changed = false;
        for (int i = 0; i < snaps.Length; i++)
        {
            if (!_rowById.TryGetValue(snaps[i].Id, out var row)) continue;
            changed |= UpdateRowValue(row, snaps[i].Value);
            changed |= UpdateRowRange(row);
        }
        if (changed) Invalidate();
    }

    /// <summary>Updates cached value text/color; skips all formatting when the value did not
    /// change (hot path). Returns true when the rendered text changed.</summary>
    private static bool UpdateRowValue(Row row, float? value)
    {
        bool has = value is { } v0 && !float.IsNaN(v0);
        float v = has ? value.GetValueOrDefault() : 0f;
        if (has == row.HasValue && (!has || v == row.LastValue)) return false;
        row.HasValue = has;
        row.LastValue = v;

        if (has && row.IsThrottle)
        {
            bool hot = v >= 0.5f;
            row.ValueText = Loc.T(hot ? "boolean.true" : "boolean.false");
            row.ColorState = hot ? 2 : 0;
        }
        else if (has)
        {
            row.ValueText = Units.Format(row.Quantity, v);
            row.ColorState = v >= row.Thresholds.Red ? 2 : v >= row.Thresholds.Yellow ? 1 : 0;
        }
        else
        {
            row.ValueText = "—";
            row.ColorState = -1;
        }
        return true;
    }

    /// <summary>Refreshes the dim min/max column from session stats; reformats only when the
    /// peaks moved. Returns true when the rendered text changed.</summary>
    private bool UpdateRowRange(Row row)
    {
        if (row.IsThrottle) return false;   // binary min/max says nothing; current state is enough
        float min = float.NaN, max = float.NaN;
        var stats = _ctx.Stats.GetStats(row.Id);
        if (stats is { HasData: true })
        {
            min = stats.Min;
            max = stats.Max;
        }
        if (SameFloat(min, row.LastMin) && SameFloat(max, row.LastMax)) return false;
        row.LastMin = min;
        row.LastMax = max;
        row.RangeText = float.IsNaN(min)
            ? ""
            : Units.Format(row.Quantity, min) + " / " + Units.Format(row.Quantity, max);
        return true;
    }

    private static bool SameFloat(float a, float b)
        => a == b || (float.IsNaN(a) && float.IsNaN(b));

    private static SensorDescriptor? FindDescriptor(IReadOnlyList<SensorDescriptor> descriptors, string id)
    {
        for (int i = 0; i < descriptors.Count; i++)
            if (descriptors[i].Id == id) return descriptors[i];
        return null;
    }

    // ---------- layout / painting ----------

    private void RecalcLayout()
    {
        // Scale the pixel constants and measure text at the DPI of the monitor the popup is on.
        // Before the handle exists (first show) DeviceDpi is the startup DPI and no DC is
        // available — ShowFor relayouts again after Show() so the target monitor wins.
        float scale = DeviceDpi / 96f;
        _pad = (int)MathF.Round(Pad * scale);
        _gap = (int)MathF.Round(Gap * scale);
        int rowPad = (int)MathF.Round(6 * scale);

        Graphics? g = IsHandleCreated ? CreateGraphics() : null;
        try
        {
            Size Measure(string s) => g is not null
                ? TextRenderer.MeasureText(g, s, _font)
                : TextRenderer.MeasureText(s, _font);

            _rowHeight = Measure("Ag").Height + rowPad;

            int nameWidth = Measure(_emptyText).Width;
            int valueWidth = Measure("—").Width;
            int rangeWidth = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                int nw = Measure(row.Name).Width;
                if (nw > nameWidth) nameWidth = nw;

                if (row.IsThrottle)
                {
                    // State words replace numbers; throttle has no min/max column.
                    int vw = Math.Max(Measure(Loc.T("boolean.false")).Width,
                                      Measure(Loc.T("boolean.true")).Width);
                    if (vw > valueWidth) valueWidth = vw;
                }
                else
                {
                    // Worst-realistic-width template per quantity so live updates never relayout.
                    string tmpl = Units.Format(row.Quantity, 8888.8f);
                    int vw = Measure(tmpl).Width;
                    if (vw > valueWidth) valueWidth = vw;
                    int rw = Measure(tmpl + " / " + tmpl).Width;
                    if (rw > rangeWidth) rangeWidth = rw;
                }
            }

            _nameColWidth = Math.Clamp(nameWidth, (int)MathF.Round(40 * scale), (int)MathF.Round(240 * scale));
            _valueColWidth = valueWidth;
            _rangeColWidth = rangeWidth;

            int width = _pad * 2 + _nameColWidth + _gap + _valueColWidth
                      + (_rangeColWidth > 0 ? _gap + _rangeColWidth : 0);
            int rowCount = Math.Max(1, _rows.Count);
            ClientSize = new Size(width, _pad * 2 + rowCount * _rowHeight);
        }
        finally
        {
            g?.Dispose();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        ControlPaint.DrawBorder(g, ClientRectangle, Theme.Border, ButtonBorderStyle.Solid);

        const TextFormatFlags nameFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags valueFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        if (_rows.Count == 0)
        {
            TextRenderer.DrawText(g, _emptyText, _font, ClientRectangle, Theme.SubtleText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        int y = _pad;
        int valueX = _pad + _nameColWidth + _gap;
        int rangeX = valueX + _valueColWidth + _gap;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            TextRenderer.DrawText(g, row.Name, _font,
                new Rectangle(Pad, y, _nameColWidth, _rowHeight), Theme.Text, nameFlags);
            TextRenderer.DrawText(g, row.ValueText, _font,
                new Rectangle(valueX, y, _valueColWidth, _rowHeight), ColorFor(row.ColorState), valueFlags);
            if (_rangeColWidth > 0 && row.RangeText.Length > 0)
                TextRenderer.DrawText(g, row.RangeText, _font,
                    new Rectangle(rangeX, y, _rangeColWidth, _rowHeight), Theme.SubtleText, valueFlags);
            y += _rowHeight;
        }
    }

    private static Color ColorFor(int state) => state switch
    {
        0 => Theme.Good,
        1 => Theme.Warn,
        2 => Theme.Hot,
        _ => Theme.SubtleText,
    };

    // ---------- auto-hide ----------

    private void ResetHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();   // the user interacted with the popup, then clicked somewhere else
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _hideTimer.Stop();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ResetHideTimer();   // interaction: give the user another full window
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        ResetHideTimer();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // The popup is reused across clicks; only app shutdown really disposes it.
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _hideTimer.Dispose();
            _font.Dispose();
        }
    }
}
