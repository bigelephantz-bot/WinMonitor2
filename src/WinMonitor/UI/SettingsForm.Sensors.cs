using System.Globalization;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.UI;

// SettingsForm — Sensors tab: rename/hide grid with threshold editing, filter box,
// suggest and restore-all buttons.
public sealed partial class SettingsForm
{
    // Sensors grid columns
    private const int SColHardware = 0, SColSensor = 1, SColRename = 2, SColHidden = 3, SColYellow = 4, SColRed = 5;

    // Sensors
    private DataGridView _gridSensors = null!;
    private TextBox _txtSensorFilter = null!;
    private string? _sensorGridTipKey;

    // ================= Sensors tab =================

    private TabPage BuildSensorsTab()
    {
        var page = NewTabPage(Loc.T("set.tab.sensors"));

        _gridSensors = NewGrid();
        _gridSensors.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.sens.col.hardware"), ReadOnly = true, FillWeight = 24 },
            new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.sens.col.sensor"), ReadOnly = true, FillWeight = 22 },
            new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.sens.col.rename"), FillWeight = 22 },
            new DataGridViewCheckBoxColumn { HeaderText = Loc.T("set.sens.col.hidden"), FillWeight = 10 },
            new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.sens.col.yellow"), FillWeight = 11 },
            new DataGridViewTextBoxColumn { HeaderText = Loc.T("set.sens.col.red"), FillWeight = 11 });
        _gridSensors.CellValueChanged += OnSensorsCellValueChanged;
        _gridSensors.MouseMove += OnSensorsGridMouseMove;
        SetOptionToolTip("tip.sens.grid", _gridSensors);

        _txtSensorFilter = NewFilterBox();
        _txtSensorFilter.TextChanged += (_, _) => { if (!_loading) LoadSensorsTab(); };
        SetOptionToolTip("tip.sens.filter", _txtSensorFilter);

        var hint = new Label
        {
            Text = Loc.T("set.sens.hint"),
            Dock = DockStyle.Top,
            ForeColor = Theme.SubtleText,
            Padding = new Padding(8, 6, 8, 4),
            Height = 34,
        };
        SetOptionToolTip("tip.sens.grid", hint);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        var btnSuggest = new Button { Text = Loc.T("set.sens.suggest"), Location = new Point(8, 6), Size = new Size(180, 28) };
        btnSuggest.Click += OnSuggestThresholds;
        SetOptionToolTip("tip.sens.suggest", btnSuggest);
        bottom.Controls.Add(btnSuggest);
        var btnRestoreAll = new Button { Text = Loc.T("set.sens.restore_all"), Location = new Point(196, 6), Size = new Size(220, 28) };
        btnRestoreAll.Click += OnRestoreAllThresholds;
        SetOptionToolTip("tip.sens.restore_all", btnRestoreAll);
        bottom.Controls.Add(btnRestoreAll);

        page.Controls.Add(_gridSensors);
        page.Controls.Add(_txtSensorFilter);
        page.Controls.Add(hint);
        page.Controls.Add(bottom);
        _gridSensors.BringToFront();
        return page;
    }

    private static string SensorGridTipKey(int columnIndex) => columnIndex switch
    {
        SColHardware => "tip.sens.hardware",
        SColSensor => "tip.sens.sensor",
        SColRename => "tip.sens.rename",
        SColHidden => "tip.sens.hidden",
        SColYellow => "tip.sens.yellow",
        SColRed => "tip.sens.red",
        _ => "tip.sens.grid",
    };

    private void OnSensorsGridMouseMove(object? sender, MouseEventArgs e)
    {
        var hit = _gridSensors.HitTest(e.X, e.Y);
        string key = hit.Type is DataGridViewHitTestType.Cell or DataGridViewHitTestType.ColumnHeader
            ? SensorGridTipKey(hit.ColumnIndex)
            : "tip.sens.grid";
        if (key == _sensorGridTipKey) return;
        _sensorGridTipKey = key;
        SetOptionToolTip(key, _gridSensors);
    }

    private void LoadSensorsTab()
    {
        bool prev = _loading;
        _loading = true;
        try
        {
            string filter = _txtSensorFilter.Text.Trim();
            _gridSensors.Rows.Clear();
            foreach (var d in SortedDescriptors())
            {
                if (!MatchesFilter(d, filter)) continue;
                Config.SensorOverrides.TryGetValue(d.Id, out var o);
                // Yellow/Red always show the resolved effective thresholds, so the suggested
                // defaults are visible (and editable) even when no override exists.
                var t = Config.ResolveThresholds(d);
                int idx = _gridSensors.Rows.Add(
                    d.HardwareName,
                    d.Name,
                    o?.Rename ?? "",
                    o?.Hidden ?? false,
                    FormatFloat(t.Yellow),
                    FormatFloat(t.Red));
                _gridSensors.Rows[idx].Tag = d;
            }
        }
        finally { _loading = prev; }
    }

    private void OnSensorsCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0) return;
        var row = _gridSensors.Rows[e.RowIndex];
        if (row.Tag is not SensorDescriptor d) return;

        switch (e.ColumnIndex)
        {
            case SColRename:
            {
                string rename = (row.Cells[SColRename].Value as string)?.Trim() ?? "";
                var o = GetOrCreateOverride(d.Id);
                o.Rename = rename.Length == 0 ? null : rename;
                PruneOverride(d.Id);
                break;
            }
            case SColHidden:
            {
                bool hidden = row.Cells[SColHidden].Value is true;
                var o = GetOrCreateOverride(d.Id);
                o.Hidden = hidden;
                PruneOverride(d.Id);
                if (hidden) RemoveSensorFromAllTrayIcons(d.Id);
                break;
            }
            case SColYellow:
            case SColRed:
                CommitThresholdCells(row, d);
                break;
        }
    }

    private void CommitThresholdCells(DataGridViewRow row, SensorDescriptor d)
    {
        bool okY = TryParseCellFloat(row.Cells[SColYellow].Value, out float? y);
        bool okR = TryParseCellFloat(row.Cells[SColRed].Value, out float? r);
        if (!okY || !okR)
        {
            RepopulateThresholdCells(row, d);   // parse error → revert to stored values
            return;
        }

        var sug = Thresholds.SuggestFor(d.Category, d.Quantity);
        // A per-profile override wins in ResolveThresholds, so the edit must land on the profile
        // entry or it would be invisible (the grid shows the profile value either way). Otherwise
        // fall through to the global override, exactly as before.
        bool hasProfileOverride =
            Config.Active.ThresholdOverrides.TryGetValue(d.Id, out var pt) && pt is not null;

        if (y is null && r is null)
        {
            // Both cleared → inherit the global/suggested layer; keep an object only when it
            // still carries alert configuration (edited on the Alerts tab).
            if (hasProfileOverride)
            {
                if (pt!.AlertEnabled || pt.PlaySound || !string.IsNullOrEmpty(pt.SoundPath))
                {
                    pt.Yellow = sug.Yellow;
                    pt.Red = sug.Red;
                }
                else
                {
                    Config.Active.ThresholdOverrides.Remove(d.Id);
                }
            }
            else if (Config.SensorOverrides.TryGetValue(d.Id, out var o) && o.Thresholds is { } t)
            {
                if (t.AlertEnabled || t.PlaySound || !string.IsNullOrEmpty(t.SoundPath))
                {
                    t.Yellow = sug.Yellow;
                    t.Red = sug.Red;
                }
                else
                {
                    o.Thresholds = null;
                }
                PruneOverride(d.Id);
            }
        }
        else if (hasProfileOverride)
        {
            pt!.Yellow = y ?? sug.Yellow;
            pt.Red = r ?? sug.Red;
            // Drop only when the edited value matches the global/suggested layer it inherits.
            CleanupProfileOverride(d);
        }
        else
        {
            var o = GetOrCreateOverride(d.Id);
            o.Thresholds ??= Config.ResolveThresholds(d).Clone();
            o.Thresholds.Yellow = y ?? sug.Yellow;
            o.Thresholds.Red = r ?? sug.Red;
            // Typed exactly the suggested values → drop the override so "suggested" stays live.
            CleanupAlertOverride(d);
        }
        RepopulateThresholdCells(row, d);
    }

    private void RepopulateThresholdCells(DataGridViewRow row, SensorDescriptor d)
    {
        var t = Config.ResolveThresholds(d);
        bool prev = _loading;
        _loading = true;
        try
        {
            row.Cells[SColYellow].Value = FormatFloat(t.Yellow);
            row.Cells[SColRed].Value = FormatFloat(t.Red);
        }
        finally { _loading = prev; }
    }

    private void OnSuggestThresholds(object? sender, EventArgs e)
    {
        var rows = new List<DataGridViewRow>();
        foreach (DataGridViewRow r in _gridSensors.SelectedRows) rows.Add(r);
        if (rows.Count == 0 && _gridSensors.CurrentRow is { } cur) rows.Add(cur);

        foreach (var row in rows)
        {
            if (row.Tag is not SensorDescriptor d) continue;
            var sug = Thresholds.SuggestFor(d.Category, d.Quantity);
            if (sug.Yellow >= float.MaxValue) continue;   // "never colored" quantities
            if (Config.Active.ThresholdOverrides.TryGetValue(d.Id, out var pt) && pt is not null)
            {
                // Profile override wins in ResolveThresholds, so suggest must land on it too.
                pt.Yellow = sug.Yellow;
                pt.Red = sug.Red;
                CleanupProfileOverride(d);
            }
            else
            {
                var o = GetOrCreateOverride(d.Id);
                o.Thresholds ??= Config.ResolveThresholds(d).Clone();
                o.Thresholds.Yellow = sug.Yellow;
                o.Thresholds.Red = sug.Red;
                // Now matching the suggested values → prune so "suggested" stays live.
                CleanupAlertOverride(d);
            }
            RepopulateThresholdCells(row, d);
        }
    }

    /// <summary>Resets Yellow/Red of every sensor (all profiles) to the suggested defaults.
    /// Renames, hidden flags and alert settings (enable/sustain/sound) are preserved.</summary>
    private void OnRestoreAllThresholds(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("set.sens.restore_all.confirm"), Loc.T("set.sens.restore_all"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var ids = new List<string>(Config.SensorOverrides.Keys);
        foreach (var id in ids)
        {
            var o = Config.SensorOverrides[id];
            if (o.Thresholds is not { } t) continue;
            var dg = FindDescriptorById(id);
            if (dg is null) continue;      // unplugged hardware: nothing to suggest, keep as-is
            var sugg = Thresholds.SuggestFor(dg.Category, dg.Quantity);
            if (t.AlertEnabled || t.PlaySound || !string.IsNullOrEmpty(t.SoundPath)
                || t.SustainSeconds != sugg.SustainSeconds)
            {
                // Keep the object (it carries alert / sustain settings); only write suggested Y/R back.
                t.Yellow = sugg.Yellow;
                t.Red = sugg.Red;
            }
            else
            {
                o.Thresholds = null;
                PruneOverride(id);
            }
        }

        // Per-profile overrides: retain explicit suggested values whenever the global layer differs.
        // Otherwise deleting the override would make Restore All immediately show the global values.
        foreach (var p in Config.Profiles)
        {
            var keys = new List<string>(p.ThresholdOverrides.Keys);
            foreach (var id in keys)
            {
                var t = p.ThresholdOverrides[id];
                if (t is null) { p.ThresholdOverrides.Remove(id); continue; }
                var d = FindDescriptorById(id);
                if (d is null) continue;   // unplugged hardware: nothing to suggest, keep as-is
                var sug = Thresholds.SuggestFor(d.Category, d.Quantity);
                if (t.AlertEnabled || t.PlaySound || !string.IsNullOrEmpty(t.SoundPath)
                    || t.SustainSeconds != sug.SustainSeconds)
                {
                    t.Yellow = sug.Yellow;
                    t.Red = sug.Red;
                }
                else
                {
                    Thresholds inherited = Config.SensorOverrides.TryGetValue(id, out var global)
                        && global.Thresholds is not null
                        ? global.Thresholds
                        : sug;
                    if (inherited.Yellow == sug.Yellow && inherited.Red == sug.Red
                        && inherited.SustainSeconds == sug.SustainSeconds)
                    {
                        p.ThresholdOverrides.Remove(id);
                    }
                    else
                    {
                        t.Yellow = sug.Yellow;
                        t.Red = sug.Red;
                    }
                }
            }
        }

        LoadSensorsTab();
    }

    /// <summary>Profile-scoped mirror of CleanupAlertOverride: drops the active profile's threshold
    /// override once it matches the value it would inherit from the global/suggested layer and
    /// carries no alert settings. Comparing to the inherited layer (not only suggestions) avoids
    /// accidentally discarding a deliberate profile value when a global override exists.</summary>
    private void CleanupProfileOverride(SensorDescriptor d)
    {
        var overrides = Config.Active.ThresholdOverrides;
        if (!overrides.TryGetValue(d.Id, out var t) || t is null) return;
        if (t.AlertEnabled || t.PlaySound || !string.IsNullOrEmpty(t.SoundPath)) return;
        Thresholds inherited = Config.SensorOverrides.TryGetValue(d.Id, out var global)
            && global.Thresholds is not null
            ? global.Thresholds
            : Thresholds.SuggestFor(d.Category, d.Quantity);
        if (t.Yellow == inherited.Yellow && t.Red == inherited.Red
            && t.SustainSeconds == inherited.SustainSeconds)
            overrides.Remove(d.Id);
    }

    /// <summary>Hidden sensors must not stay referenced by any profile's tray icons.</summary>
    private void RemoveSensorFromAllTrayIcons(string id)
    {
        foreach (var profile in Config.Profiles)
            foreach (var icon in profile.TrayIcons)
                icon.SensorIds.Remove(id);
    }

    private static string FormatFloat(float? v)
        => v is { } f && !float.IsNaN(f) && f < float.MaxValue
            ? f.ToString("0.##", CultureInfo.CurrentCulture)
            : "";

    /// <summary>Empty string parses as "cleared" (result null, returns true); garbage returns false.</summary>
    private static bool TryParseCellFloat(object? value, out float? result)
    {
        result = null;
        string? s = (value as string)?.Trim();
        if (string.IsNullOrEmpty(s)) return true;
        if (float.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out float f)
            || float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return false;
            result = f;
            return true;
        }
        return false;
    }
}
