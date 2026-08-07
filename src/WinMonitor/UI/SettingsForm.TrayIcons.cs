using System.Text;
using WinMonitor.Config;
using WinMonitor.Localization;

namespace WinMonitor.UI;

// SettingsForm — Tray icons tab: icon list (add/remove/reorder/split), per-icon
// sensor selection, style/rotation/color options.
public sealed partial class SettingsForm
{
    // Tray icons
    private Profile? _trayTabProfile;   // profile the tab displays; may differ from Config.Active
    private ListBox _lstTrayIcons = null!;
    private Button _btnTrayAdd = null!, _btnTrayRemove = null!, _btnTrayUp = null!, _btnTrayDown = null!, _btnTraySplit = null!;
    private CheckedListBox _clbTraySensors = null!;
    private readonly List<string> _traySensorListIds = new();   // parallel to _clbTraySensors items
    private NumericUpDown _numRotate = null!;
    private ComboBox _cboStyle = null!;
    private CheckBox _chkBold = null!, _chkSparkline = null!;
    private RadioButton _rbThresholdColors = null!, _rbFixedColor = null!;
    private Button _btnPickColor = null!;

    // ================= Tray icons tab =================

    private TabPage BuildTrayTab()
    {
        var page = NewTabPage(Loc.T("set.tab.tray"));

        var lblTrayList = NewLabel(Loc.T("set.tray.list"), 12, 12);
        page.Controls.Add(lblTrayList);
        _lstTrayIcons = new ListBox { Location = new Point(12, 34), Size = new Size(250, 246), IntegralHeight = false };
        ApplyInputTheme(_lstTrayIcons);
        _lstTrayIcons.SelectedIndexChanged += (_, _) => { if (!_loading) LoadTrayIconDetails(); };
        SetOptionToolTip("tip.tray.list", lblTrayList, _lstTrayIcons);
        page.Controls.Add(_lstTrayIcons);

        _btnTrayAdd = NewButton(Loc.T("set.tray.add"), 12, 288, 122);
        _btnTrayRemove = NewButton(Loc.T("set.tray.remove"), 140, 288, 122);
        _btnTrayUp = NewButton(Loc.T("set.tray.up"), 12, 320, 122);
        _btnTrayDown = NewButton(Loc.T("set.tray.down"), 140, 320, 122);
        _btnTrayAdd.Click += OnTrayAdd;
        _btnTrayRemove.Click += OnTrayRemove;
        _btnTrayUp.Click += (_, _) => MoveTrayIcon(-1);
        _btnTrayDown.Click += (_, _) => MoveTrayIcon(+1);
        SetOptionToolTip("tip.tray.add", _btnTrayAdd);
        SetOptionToolTip("tip.tray.remove", _btnTrayRemove);
        SetOptionToolTip("tip.tray.up", _btnTrayUp);
        SetOptionToolTip("tip.tray.down", _btnTrayDown);
        page.Controls.Add(_btnTrayAdd);
        page.Controls.Add(_btnTrayRemove);
        page.Controls.Add(_btnTrayUp);
        page.Controls.Add(_btnTrayDown);

        _btnTraySplit = NewButton(Loc.T("set.tray.split"), 12, 352, 250);
        _btnTraySplit.Click += OnTraySplit;
        SetOptionToolTip("tip.tray.split", _btnTraySplit);
        page.Controls.Add(_btnTraySplit);

        var lblTraySensors = NewLabel(Loc.T("set.tray.sensors"), 280, 12);
        page.Controls.Add(lblTraySensors);
        _clbTraySensors = new CheckedListBox
        {
            Location = new Point(280, 34),
            Size = new Size(400, 170),
            CheckOnClick = true,
            IntegralHeight = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        ApplyInputTheme(_clbTraySensors);
        _clbTraySensors.ItemCheck += OnTraySensorItemCheck;
        SetOptionToolTip("tip.tray.sensors", lblTraySensors, _clbTraySensors);
        page.Controls.Add(_clbTraySensors);

        var hintIndependent = new Label
        {
            Text = Loc.T("set.tray.independent_hint"),
            Location = new Point(280, 208),
            Size = new Size(400, 48),
            ForeColor = Theme.SubtleText,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        SetOptionToolTip("tip.tray.sensors", hintIndependent);
        page.Controls.Add(hintIndependent);

        var lblRotate = NewLabel(Loc.T("set.tray.rotate"), 280, 262);
        page.Controls.Add(lblRotate);
        _numRotate = NewNumeric(560, 258, 70, 1, 60);
        _numRotate.ValueChanged += (_, _) => { if (!_loading && SelectedTrayIcon is { } c) c.RotateIntervalSec = (int)_numRotate.Value; };
        SetOptionToolTip("tip.tray.rotate", lblRotate, _numRotate);
        page.Controls.Add(_numRotate);

        var lblStyle = NewLabel(Loc.T("set.tray.style"), 280, 294);
        page.Controls.Add(lblStyle);
        _cboStyle = NewCombo(440, 290, 240);
        _cboStyle.Items.AddRange(new object[] { Loc.T("set.tray.style.text"), Loc.T("set.tray.style.badge") });
        _cboStyle.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && SelectedTrayIcon is { } c)
                c.Style = _cboStyle.SelectedIndex == 1 ? TrayIconStyle.TextOnBadge : TrayIconStyle.TextOnly;
        };
        SetOptionToolTip("tip.tray.style", lblStyle, _cboStyle);
        page.Controls.Add(_cboStyle);

        // No "show unit" checkbox: the tray glyph is digits only. At 16 px a unit suffix costs
        // glyphs the number cannot spare, so units are shown in the tooltip and main window
        // instead. TrayIconConfig.ShowUnit is kept for config compatibility but is unused.
        _chkBold = NewCheck(Loc.T("set.tray.bold"), 280, 322);
        _chkBold.CheckedChanged += (_, _) => { if (!_loading && SelectedTrayIcon is { } c) c.Bold = _chkBold.Checked; };
        SetOptionToolTip("tip.tray.bold", _chkBold);
        page.Controls.Add(_chkBold);

        _chkSparkline = NewCheck(Loc.T("set.tray.sparkline"), 280, 350);
        _chkSparkline.CheckedChanged += (_, _) => { if (!_loading && SelectedTrayIcon is { } c) c.ShowSparkline = _chkSparkline.Checked; };
        SetOptionToolTip("tip.tray.sparkline", _chkSparkline);
        page.Controls.Add(_chkSparkline);

        _rbThresholdColors = new RadioButton { Text = Loc.T("set.tray.threshold_colors"), Location = new Point(280, 378), AutoSize = true, ForeColor = Theme.Text };
        _rbThresholdColors.CheckedChanged += (_, _) =>
        {
            if (!_loading && _rbThresholdColors.Checked && SelectedTrayIcon is { } c) c.ColorOverride = null;
        };
        SetOptionToolTip("tip.tray.threshold_colors", _rbThresholdColors);
        page.Controls.Add(_rbThresholdColors);

        _rbFixedColor = new RadioButton { Text = Loc.T("set.tray.color_override"), Location = new Point(280, 404), AutoSize = true, ForeColor = Theme.Text };
        _rbFixedColor.CheckedChanged += (_, _) =>
        {
            if (!_loading && _rbFixedColor.Checked && SelectedTrayIcon is { } c)
                c.ColorOverride = ColorToHex(_btnPickColor.BackColor);
        };
        SetOptionToolTip("tip.tray.fixed_color", _rbFixedColor);
        page.Controls.Add(_rbFixedColor);

        _btnPickColor = new Button
        {
            Text = Loc.T("set.tray.pick_color"),
            Location = new Point(600, 400),
            Size = new Size(80, 26),
            BackColor = Color.White,
        };
        _btnPickColor.Click += OnPickColor;
        SetOptionToolTip("tip.tray.pick_color", _btnPickColor);
        page.Controls.Add(_btnPickColor);

        return page;
    }

    private TrayIconConfig? SelectedTrayIcon
    {
        get
        {
            var icons = _trayTabProfile?.TrayIcons;
            int i = _lstTrayIcons.SelectedIndex;
            return icons is not null && i >= 0 && i < icons.Count ? icons[i] : null;
        }
    }

    private void LoadTrayTab()
    {
        _trayTabProfile = Config.Active;
        PopulateTraySensorList();
        RefreshTrayIconItems(Math.Max(_lstTrayIcons.SelectedIndex, 0));
    }

    private void PopulateTraySensorList()
    {
        bool prev = _loading;
        _loading = true;
        try
        {
            _clbTraySensors.BeginUpdate();
            _clbTraySensors.Items.Clear();
            _traySensorListIds.Clear();
            foreach (var d in SortedDescriptors())
            {
                bool hidden = Config.IsHidden(d.Id);
                // Hidden sensors stay listed while this profile's icons still reference
                // them, so the stale reference can be unchecked.
                if (hidden && !IsReferencedByTrayTabProfile(d.Id)) continue;
                string label = "[" + Loc.T(CatKey(d.Category)) + "] " + Config.DisplayNameFor(d);
                if (hidden) label += " (" + Loc.T("set.sens.col.hidden") + ")";
                _clbTraySensors.Items.Add(label);
                _traySensorListIds.Add(d.Id);
            }
            _clbTraySensors.EndUpdate();
        }
        finally { _loading = prev; }
    }

    private bool IsReferencedByTrayTabProfile(string id)
    {
        var icons = _trayTabProfile?.TrayIcons;
        if (icons is null) return false;
        foreach (var icon in icons)
            if (icon.SensorIds.Contains(id)) return true;
        return false;
    }

    private void RefreshTrayIconItems(int selectIndex)
    {
        if (_trayTabProfile is null) return;
        bool prev = _loading;
        _loading = true;
        try
        {
            _lstTrayIcons.BeginUpdate();
            _lstTrayIcons.Items.Clear();
            var icons = _trayTabProfile.TrayIcons;
            for (int i = 0; i < icons.Count; i++)
                _lstTrayIcons.Items.Add(TrayEntryLabel(i, icons[i]));
            if (icons.Count > 0)
                _lstTrayIcons.SelectedIndex = Math.Clamp(selectIndex, 0, icons.Count - 1);
            _lstTrayIcons.EndUpdate();
        }
        finally { _loading = prev; }
        LoadTrayIconDetails();
    }

    private string TrayEntryLabel(int index, TrayIconConfig cfg)
    {
        string names;
        if (cfg.SensorIds.Count == 0)
        {
            names = Loc.T("set.tray.auto");
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var id in cfg.SensorIds)
            {
                if (sb.Length > 0) sb.Append(", ");
                if (sb.Length > 60) { sb.Append('…'); break; }
                var d = FindDescriptorById(id);
                sb.Append(d is null ? id : Config.DisplayNameFor(d));
            }
            names = sb.ToString();
        }
        return Loc.F("set.tray.icon_label", index + 1, names);
    }

    private void LoadTrayIconDetails()
    {
        var cfg = SelectedTrayIcon;
        bool has = cfg is not null;
        _clbTraySensors.Enabled = has;
        _cboStyle.Enabled = has;
        _chkBold.Enabled = has;
        _chkSparkline.Enabled = has;
        _rbThresholdColors.Enabled = has;
        _rbFixedColor.Enabled = has;
        _btnPickColor.Enabled = has;

        bool prev = _loading;
        _loading = true;
        try
        {
            for (int i = 0; i < _clbTraySensors.Items.Count; i++)
                _clbTraySensors.SetItemChecked(i, cfg is not null && cfg.SensorIds.Contains(_traySensorListIds[i]));

            if (cfg is null)
            {
                _numRotate.Enabled = false;
                _btnTraySplit.Enabled = false;
                return;
            }
            _numRotate.Value = Math.Clamp(cfg.RotateIntervalSec, 1, 60);
            _cboStyle.SelectedIndex = cfg.Style == TrayIconStyle.TextOnBadge ? 1 : 0;
            _chkBold.Checked = cfg.Bold;
            _chkSparkline.Checked = cfg.ShowSparkline;
            bool fixedColor = !string.IsNullOrEmpty(cfg.ColorOverride);
            _rbFixedColor.Checked = fixedColor;
            _rbThresholdColors.Checked = !fixedColor;
            _btnPickColor.BackColor = ParseColor(cfg.ColorOverride) ?? Color.White;
            _numRotate.Enabled = cfg.SensorIds.Count >= 2;
            _btnTraySplit.Enabled = cfg.SensorIds.Count >= 2;
        }
        finally { _loading = prev; }
    }

    private void OnTraySensorItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_loading) return;
        var cfg = SelectedTrayIcon;
        if (cfg is null)
        {
            e.NewValue = e.CurrentValue;
            return;
        }
        // ItemCheck fires before the visual state flips; we keep order in cfg.SensorIds
        // ourselves (CheckedListBox has no notion of check order): check appends, uncheck removes.
        string id = _traySensorListIds[e.Index];
        if (e.NewValue == CheckState.Checked)
        {
            if (!cfg.SensorIds.Contains(id)) cfg.SensorIds.Add(id);
        }
        else
        {
            cfg.SensorIds.Remove(id);
        }
        _numRotate.Enabled = cfg.SensorIds.Count >= 2;
        _btnTraySplit.Enabled = cfg.SensorIds.Count >= 2;
        UpdateSelectedTrayLabel();
    }

    private void UpdateSelectedTrayLabel()
    {
        int i = _lstTrayIcons.SelectedIndex;
        var cfg = SelectedTrayIcon;
        if (i < 0 || cfg is null) return;
        bool prev = _loading;
        _loading = true;
        try { _lstTrayIcons.Items[i] = TrayEntryLabel(i, cfg); }
        finally { _loading = prev; }
    }

    private void OnTrayAdd(object? sender, EventArgs e)
    {
        if (_trayTabProfile is null) return;
        var icons = _trayTabProfile.TrayIcons;
        icons.Add(new TrayIconConfig());
        RefreshTrayIconItems(icons.Count - 1);
    }

    private void OnTrayRemove(object? sender, EventArgs e)
    {
        if (_trayTabProfile is null) return;
        var icons = _trayTabProfile.TrayIcons;
        int i = _lstTrayIcons.SelectedIndex;
        if (i < 0 || i >= icons.Count || icons.Count <= 1) return;  // keep at least one icon
        icons.RemoveAt(i);
        RefreshTrayIconItems(Math.Min(i, icons.Count - 1));
    }

    private void MoveTrayIcon(int delta)
    {
        if (_trayTabProfile is null) return;
        var icons = _trayTabProfile.TrayIcons;
        int i = _lstTrayIcons.SelectedIndex;
        int j = i + delta;
        if (i < 0 || i >= icons.Count || j < 0 || j >= icons.Count) return;
        (icons[i], icons[j]) = (icons[j], icons[i]);
        RefreshTrayIconItems(j);
    }

    /// <summary>Replaces the selected multi-sensor (carousel) entry with one single-sensor
    /// icon per checked sensor, cloning the entry's style/bold/unit/color settings.</summary>
    private void OnTraySplit(object? sender, EventArgs e)
    {
        if (_trayTabProfile is null) return;
        var icons = _trayTabProfile.TrayIcons;
        int i = _lstTrayIcons.SelectedIndex;
        if (i < 0 || i >= icons.Count) return;
        var src = icons[i];
        if (src.SensorIds.Count < 2) return;
        icons.RemoveAt(i);
        for (int k = 0; k < src.SensorIds.Count; k++)
        {
            var single = src.Clone();
            single.SensorIds.Clear();
            single.SensorIds.Add(src.SensorIds[k]);
            icons.Insert(i + k, single);
        }
        RefreshTrayIconItems(i);   // selects the first of the new single-sensor entries
    }

    private void OnPickColor(object? sender, EventArgs e)
    {
        using var dlg = new ColorDialog { Color = _btnPickColor.BackColor, FullOpen = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _btnPickColor.BackColor = dlg.Color;
        _rbFixedColor.Checked = true;   // picking a color implies fixed-color mode
        if (SelectedTrayIcon is { } c) c.ColorOverride = ColorToHex(dlg.Color);
    }

    private static string ColorToHex(Color c)
        => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    private static Color? ParseColor(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        try { return ColorTranslator.FromHtml(html); }
        catch { return null; }
    }
}
