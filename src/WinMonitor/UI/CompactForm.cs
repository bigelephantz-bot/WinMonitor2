using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.UI;

/// <summary>
/// Borderless always-on-top mini window listing the active profile's tray sensors
/// (name + value colored by threshold). Snapshots arrive on the polling thread and are
/// coalesced onto the UI thread; painting only uses strings/colors cached per row.
/// </summary>
public sealed class CompactForm : Form
{
    private sealed class Row
    {
        public string Id = "";
        public string Name = "";
        public SensorQuantity Quantity;
        public Thresholds Thresholds = new();
        public bool IsThrottle;      // synthetic throttle sensor renders a state word, not a %
        public string ValueText = "—";
        public int ColorState = -1;   // -1 no data, 0 green, 1 yellow, 2 red
        public bool HasValue;
        public float LastValue = float.NaN;
    }

    private const int Pad = 10;
    private const int Gap = 8;
    private const int CornerRadius = 12;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const int WmExitSizeMove = 0x0232;
    private const int WsExToolWindow = 0x00000080;

    // All colors come from Theme (re-read in ReloadSensors), so the widget follows the
    // app-wide light/dark setting instead of its former hardcoded dark palette.
    private readonly WinMonitorContext _ctx;
    private readonly Font _font = new("Segoe UI", 9.5f);
    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, Row> _rowById = new(StringComparer.Ordinal);
    private readonly Action _applyPendingDelegate;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _miFull;
    private readonly ToolStripMenuItem _miSettings;
    private readonly ToolStripMenuItem _miExit;

    // Written by the polling thread, drained on the UI thread; non-null means a UI
    // callback is already queued (coalescing — at most one BeginInvoke in flight).
    private SensorSnapshot[]? _pending;

    private string _emptyText = "—";
    private int _rowHeight = 20;
    private int _nameColWidth = 80;
    private bool _dragArmed;
    private Point _dragStart;

    public CompactForm(WinMonitorContext ctx)
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
        Opacity = 0.92;
        BackColor = Theme.ChartBack;
        Font = _font;

        _menu = new ContextMenuStrip();
        _miFull = new ToolStripMenuItem();
        _miFull.Click += (_, _) => _ctx.ShowMainWindow();
        _miSettings = new ToolStripMenuItem();
        _miSettings.Click += (_, _) => _ctx.ShowSettings();
        _miExit = new ToolStripMenuItem();
        _miExit.Click += (_, _) => _ctx.ExitApp();
        _menu.Items.Add(_miFull);
        _menu.Items.Add(_miSettings);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_miExit);
        ContextMenuStrip = _menu;

        ReloadSensors();
        Location = ResolveStartLocation();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow;   // keep the widget out of Alt-Tab
            return cp;
        }
    }

    // ---------- data intake (polling thread) ----------

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

    private void ApplyPendingSnapshots()
    {
        var snaps = Interlocked.Exchange(ref _pending, null);
        if (snaps is null || IsDisposed) return;

        bool changed = false;
        for (int i = 0; i < snaps.Length; i++)
        {
            if (!_rowById.TryGetValue(snaps[i].Id, out var row)) continue;

            float? value = snaps[i].Value;
            bool has = value is { } v0 && !float.IsNaN(v0);
            float v = has ? value.GetValueOrDefault() : 0f;

            // Skip string formatting entirely when the value did not change (hot path).
            if (has == row.HasValue && (!has || v == row.LastValue)) continue;
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
            changed = true;
        }
        if (changed) Invalidate();
    }

    // ---------- row list ----------

    /// <summary>Recomputes the row list from config + descriptors. UI thread only.</summary>
    public void ReloadSensors()
    {
        _rows.Clear();
        _rowById.Clear();
        _emptyText = Loc.T("common.none");
        _miFull.Text = Loc.T("main.full");
        _miSettings.Text = Loc.T("main.settings");
        _miExit.Text = Loc.T("main.exit");
        BackColor = Theme.ChartBack;   // re-read after a theme change (SettingsApplied path)

        var config = _ctx.Config;
        var descriptors = _ctx.Sensors.Descriptors;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var orderedIds = new List<string>();

        var trayIcons = config.Active.TrayIcons;
        for (int i = 0; i < trayIcons.Count; i++)
        {
            var icon = trayIcons[i];
            if (icon.SensorIds.Count == 0)
            {
                // Empty = auto, same rule as the tray (SensorPicker).
                string? auto = SensorPicker.PickAuto(descriptors);
                if (auto is not null && seen.Add(auto)) orderedIds.Add(auto);
            }
            else
            {
                for (int j = 0; j < icon.SensorIds.Count; j++)
                    if (seen.Add(icon.SensorIds[j])) orderedIds.Add(icon.SensorIds[j]);
            }
        }
        if (orderedIds.Count == 0)
        {
            string? auto = SensorPicker.PickAuto(descriptors);
            if (auto is not null) orderedIds.Add(auto);
        }

        for (int i = 0; i < orderedIds.Count; i++)
        {
            var d = FindDescriptor(descriptors, orderedIds[i]);
            if (d is null) continue;   // sensor vanished or not present on this machine
            var row = new Row
            {
                Id = d.Id,
                Name = config.DisplayNameFor(d),
                Quantity = d.Quantity,
                Thresholds = config.ResolveThresholds(d),
                IsThrottle = string.Equals(d.Id, WellKnown.ThrottleSensorId, StringComparison.Ordinal),
            };
            _rows.Add(row);
            _rowById[row.Id] = row;
        }

        RecalcLayout();
        Invalidate();
    }

    private static SensorDescriptor? FindDescriptor(IReadOnlyList<SensorDescriptor> descriptors, string id)
    {
        for (int i = 0; i < descriptors.Count; i++)
            if (descriptors[i].Id == id) return descriptors[i];
        return null;
    }

    private void RecalcLayout()
    {
        _rowHeight = TextRenderer.MeasureText("Ag", _font).Height + 5;

        int nameWidth = TextRenderer.MeasureText(_emptyText, _font).Width;
        for (int i = 0; i < _rows.Count; i++)
        {
            int w = TextRenderer.MeasureText(_rows[i].Name, _font).Width;
            if (w > nameWidth) nameWidth = w;
        }
        _nameColWidth = Math.Clamp(nameWidth, 60, 240);

        int valueWidth = TextRenderer.MeasureText("8888.8 GB", _font).Width;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].IsThrottle) continue;
            // Throttle rows show localized state words; make sure the column fits them.
            valueWidth = Math.Max(valueWidth, Math.Max(
                TextRenderer.MeasureText(Loc.T("boolean.false"), _font).Width,
                TextRenderer.MeasureText(Loc.T("boolean.true"), _font).Width));
            break;
        }
        int rowCount = Math.Max(1, _rows.Count);
        ClientSize = new Size(Pad * 2 + _nameColWidth + Gap + valueWidth, Pad * 2 + rowCount * _rowHeight);
        UpdateRegion();
    }

    private Point ResolveStartLocation()
    {
        if (_ctx.Config.CompactLocation is { } saved)
        {
            var rect = new Rectangle(saved, Size);
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
                if (screens[i].WorkingArea.IntersectsWith(rect))
                    return saved;
        }
        var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        return new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    // ---------- painting ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
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

        int y = Pad;
        int valueX = Pad + _nameColWidth + Gap;
        int valueW = ClientSize.Width - valueX - Pad;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            TextRenderer.DrawText(g, row.Name, _font,
                new Rectangle(Pad, y, _nameColWidth, _rowHeight), Theme.Text, nameFlags);
            TextRenderer.DrawText(g, row.ValueText, _font,
                new Rectangle(valueX, y, valueW, _rowHeight), ColorFor(row.ColorState), valueFlags);
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

    private void UpdateRegion()
    {
        var rect = ClientRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var path = new GraphicsPath();
        int r = CornerRadius;
        path.AddArc(rect.X, rect.Y, r, r, 180, 90);
        path.AddArc(rect.Right - r - 1, rect.Y, r, r, 270, 90);
        path.AddArc(rect.Right - r - 1, rect.Bottom - r - 1, r, r, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r - 1, r, r, 90, 90);
        path.CloseFigure();
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    // ---------- move / interaction ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragArmed = true;
            _dragStart = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // Start the native caption drag only after a small movement threshold so
        // double-click still reaches us (WM_NCLBUTTONDOWN swallows the click pair).
        if (_dragArmed && (Math.Abs(e.X - _dragStart.X) > 2 || Math.Abs(e.Y - _dragStart.Y) > 2))
        {
            _dragArmed = false;
            ReleaseCapture();
            _ = SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragArmed = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) _ctx.ShowMainWindow();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmExitSizeMove)
            _ctx.Config.CompactLocation = Location;   // drag finished — remember position
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _ctx.Config.CompactLocation = Location;
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // The app lives in the tray; closing the mini window only hides it.
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
            _menu.Dispose();
            _font.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
