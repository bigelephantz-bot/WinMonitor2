using System.Globalization;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.UI;

// SettingsForm — Alerts tab: enable/sustain/sound grid, filter box and the shared
// alert-sound picker (system vs custom WAV, test, apply-to-all).
public sealed partial class SettingsForm
{
    // Alerts grid columns
    private const int AColSensor = 0, AColEnable = 1, AColSustain = 2, AColSound = 3;

    // Alerts
    private DataGridView _gridAlerts = null!;
    private TextBox _txtAlertFilter = null!;
    private RadioButton _rbSoundSystem = null!, _rbSoundCustom = null!;
    private TextBox _txtSoundPath = null!;
    private string? _alertGridTipKey;

    // ================= Alerts tab =================

    private TabPage BuildAlertsTab()
    {
        var page = NewTabPage(Loc.T("set.tab.alerts"));

        _gridAlerts = NewGrid();
        _gridAlerts.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.sens.col.sensor"), ReadOnly = true, FillWeight = 46 },
            new DataGridViewCheckBoxColumn { HeaderText = Loc.T("set.alert.enable"), FillWeight = 14 },
            new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.alert.sustain"), FillWeight = 14 },
            new DataGridViewCheckBoxColumn { HeaderText = Loc.T("set.alert.sound"), FillWeight = 14 });
        _gridAlerts.CellValueChanged += OnAlertsCellValueChanged;
        _gridAlerts.MouseMove += OnAlertsGridMouseMove;
        SetOptionToolTip("tip.alert.grid", _gridAlerts);

        _txtAlertFilter = NewFilterBox();
        _txtAlertFilter.TextChanged += (_, _) => { if (!_loading) LoadAlertsTab(); };
        SetOptionToolTip("tip.alert.filter", _txtAlertFilter);

        var hint = new Label
        {
            Text = Loc.T("set.alert.hint"),
            Dock = DockStyle.Top,
            ForeColor = Theme.SubtleText,
            Padding = new Padding(8, 6, 8, 4),
            Height = 46,
        };
        SetOptionToolTip("tip.alert.grid", hint);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 78 };
        var lblSound = NewLabel(Loc.T("set.alert.sound"), 8, 11);
        bottom.Controls.Add(lblSound);
        _rbSoundSystem = new RadioButton { Text = Loc.T("set.alert.sound.system"), Location = new Point(80, 8), AutoSize = true, Checked = true, ForeColor = Theme.Text };
        _rbSoundCustom = new RadioButton { Text = Loc.T("set.alert.sound.custom"), Location = new Point(210, 8), AutoSize = true, ForeColor = Theme.Text };
        _txtSoundPath = new TextBox { Location = new Point(340, 7), Width = 210 };
        ApplyInputTheme(_txtSoundPath);
        var btnBrowse = new Button { Text = Loc.T("set.alert.browse"), Location = new Point(556, 6), Size = new Size(30, 24) };
        var btnTest = new Button { Text = Loc.T("set.alert.test"), Location = new Point(592, 6), Size = new Size(80, 24) };
        var btnApplyAll = new Button { Text = Loc.T("set.alert.apply_sound_all"), Location = new Point(8, 42), Size = new Size(280, 28) };
        btnBrowse.Click += OnBrowseSound;
        btnTest.Click += OnTestSound;
        btnApplyAll.Click += OnApplySoundToAll;
        SetOptionToolTip("tip.alert.sound_mode", lblSound, _rbSoundSystem, _rbSoundCustom);
        SetOptionToolTip("tip.alert.sound_path", _txtSoundPath);
        SetOptionToolTip("tip.alert.browse", btnBrowse);
        SetOptionToolTip("tip.alert.test", btnTest);
        SetOptionToolTip("tip.alert.apply_sound_all", btnApplyAll);
        bottom.Controls.Add(_rbSoundSystem);
        bottom.Controls.Add(_rbSoundCustom);
        bottom.Controls.Add(_txtSoundPath);
        bottom.Controls.Add(btnBrowse);
        bottom.Controls.Add(btnTest);
        bottom.Controls.Add(btnApplyAll);

        page.Controls.Add(_gridAlerts);
        page.Controls.Add(_txtAlertFilter);
        page.Controls.Add(hint);
        page.Controls.Add(bottom);
        _gridAlerts.BringToFront();
        return page;
    }

    private static string AlertGridTipKey(int columnIndex) => columnIndex switch
    {
        AColSensor => "tip.alert.sensor",
        AColEnable => "tip.alert.enable",
        AColSustain => "tip.alert.sustain",
        AColSound => "tip.alert.sound",
        _ => "tip.alert.grid",
    };

    private void OnAlertsGridMouseMove(object? sender, MouseEventArgs e)
    {
        var hit = _gridAlerts.HitTest(e.X, e.Y);
        string key = hit.Type is DataGridViewHitTestType.Cell or DataGridViewHitTestType.ColumnHeader
            ? AlertGridTipKey(hit.ColumnIndex)
            : "tip.alert.grid";
        if (key == _alertGridTipKey) return;
        _alertGridTipKey = key;
        SetOptionToolTip(key, _gridAlerts);
    }

    private static bool IsAlertQuantity(SensorQuantity q)
        => q is SensorQuantity.Temperature or SensorQuantity.Fan or SensorQuantity.Power;

    private void LoadAlertsTab()
    {
        bool prev = _loading;
        _loading = true;
        try
        {
            string filter = _txtAlertFilter.Text.Trim();
            _gridAlerts.Rows.Clear();
            foreach (var d in SortedDescriptors())
            {
                if (!IsAlertQuantity(d.Quantity)) continue;
                if (!MatchesFilter(d, filter)) continue;
                var t = Config.ResolveThresholds(d);
                int idx = _gridAlerts.Rows.Add(
                    "[" + Loc.T(CatKey(d.Category)) + "] " + Config.DisplayNameFor(d),
                    t.AlertEnabled,
                    t.SustainSeconds.ToString(CultureInfo.InvariantCulture),
                    t.PlaySound);
                _gridAlerts.Rows[idx].Tag = d;
            }
        }
        finally { _loading = prev; }
    }

    /// <summary>Seeds the shared sound picker from the first override that has a custom path. Kept
    /// out of LoadAlertsTab (which the filter re-runs on every keystroke) so filtering never wipes
    /// an in-progress custom-sound selection; called only on first load / when the tab is shown.</summary>
    private void SeedAlertSoundPicker()
    {
        bool prev = _loading;
        _loading = true;
        try
        {
            string? path = null;
            // Profile overrides are effective first. If the active profile has no custom sound,
            // use a global one as a helpful starting point for the shared picker.
            foreach (var kv in Config.Active.ThresholdOverrides)
            {
                if (kv.Value?.SoundPath is { Length: > 0 } p) { path = p; break; }
            }
            if (path is null)
            {
                foreach (var kv in Config.SensorOverrides)
                {
                    if (kv.Value.Thresholds?.SoundPath is { Length: > 0 } p) { path = p; break; }
                }
            }
            _txtSoundPath.Text = path ?? "";
            _rbSoundCustom.Checked = path is not null;
            _rbSoundSystem.Checked = path is null;
        }
        finally { _loading = prev; }
    }

    private void OnAlertsCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0) return;
        var row = _gridAlerts.Rows[e.RowIndex];
        if (row.Tag is not SensorDescriptor d) return;

        switch (e.ColumnIndex)
        {
            case AColEnable:
            {
                GetOrCreateEditableThresholds(d).AlertEnabled = row.Cells[AColEnable].Value is true;
                CleanupEditableThresholdOverride(d);
                break;
            }
            case AColSustain:
            {
                string s = (row.Cells[AColSustain].Value as string)?.Trim() ?? "";
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 1 && v <= 3600)
                {
                    GetOrCreateEditableThresholds(d).SustainSeconds = v;
                }
                else
                {
                    bool prev = _loading;
                    _loading = true;
                    try
                    {
                        row.Cells[AColSustain].Value =
                            Config.ResolveThresholds(d).SustainSeconds.ToString(CultureInfo.InvariantCulture);
                    }
                    finally { _loading = prev; }
                }
                break;
            }
            case AColSound:
            {
                GetOrCreateEditableThresholds(d).PlaySound = row.Cells[AColSound].Value is true;
                CleanupEditableThresholdOverride(d);
                break;
            }
        }
    }

    /// <summary>
    /// Gets the threshold object that is effective for the active profile. Existing profile
    /// overrides must be edited in place; otherwise a global override retains the pre-existing
    /// semantics while preserving the resolved yellow/red values.
    /// </summary>
    private Thresholds GetOrCreateEditableThresholds(SensorDescriptor d)
    {
        if (Config.Active.ThresholdOverrides.TryGetValue(d.Id, out var profileThresholds)
            && profileThresholds is not null)
        {
            return profileThresholds;
        }
        var o = GetOrCreateOverride(d.Id);
        return o.Thresholds ??= Config.ResolveThresholds(d).Clone();
    }

    private void CleanupEditableThresholdOverride(SensorDescriptor d)
    {
        if (Config.Active.ThresholdOverrides.TryGetValue(d.Id, out var profileThresholds)
            && profileThresholds is not null)
        {
            CleanupProfileOverride(d);
        }
        else
        {
            CleanupAlertOverride(d);
        }
    }

    private void OnBrowseSound(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog { Filter = "WAV (*.wav)|*.wav" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _txtSoundPath.Text = dlg.FileName;
        _rbSoundCustom.Checked = true;
    }

    private void OnTestSound(object? sender, EventArgs e)
    {
        try
        {
            if (_rbSoundCustom.Checked && File.Exists(_txtSoundPath.Text))
                new System.Media.SoundPlayer(_txtSoundPath.Text).Play();
            else
                System.Media.SystemSounds.Exclamation.Play();
        }
        catch
        {
            // Audio problems must never crash the settings dialog.
        }
    }

    private void OnApplySoundToAll(object? sender, EventArgs e)
    {
        string? path = null;
        if (_rbSoundCustom.Checked)
        {
            string p = _txtSoundPath.Text.Trim();
            if (p.Length > 0) path = p;
        }
        // The filter only changes what is visible. "All alerts" must apply to every enabled
        // alert-capable sensor in the active profile, not merely the current grid rows.
        foreach (var d in SortedDescriptors())
        {
            if (!IsAlertQuantity(d.Quantity) || !Config.ResolveThresholds(d).AlertEnabled) continue;
            GetOrCreateEditableThresholds(d).SoundPath = path;
        }
        LoadAlertsTab();
    }
}
