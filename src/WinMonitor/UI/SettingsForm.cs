using System.Text.Json;
using System.Text.Json.Nodes;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.UI;

/// <summary>
/// Modeless settings dialog. Edits are made against an isolated draft, so Cancel and the X
/// button never mutate the live configuration. Apply/OK three-way merge only the draft's
/// changes into the current live configuration, then propagate it through the application.
/// Tab-specific members live in the SettingsForm.*.cs partials; this file owns the lifetime
/// and helpers shared by multiple tabs.
/// </summary>
public sealed partial class SettingsForm : Form
{
    private readonly WinMonitorContext _ctx;
    private AppConfig _draftConfig;
    private string _baselineConfigJson;
    private bool _finished;   // OK/Cancel already decided the dialog's fate
    private bool _loading;    // suppress event handlers while (re)populating controls
    private bool _applying;   // this form's own ctx.ApplySettings() call is in flight
    private readonly ToolTip _optionToolTip;
    private string _lastLocalization;
    private bool _lastDarkTheme;

    private TabControl _tabs = null!;
    private TabPage _pageTray = null!, _pageAlerts = null!, _pageProfiles = null!, _pageDiagnostics = null!;
    private Control _bottomPanel = null!;
    private string? _tabTipKey;

    /// <summary>All settings controls bind to this private draft, never directly to the live config.</summary>
    private AppConfig Config => _draftConfig;

    public SettingsForm(WinMonitorContext ctx)
    {
        _ctx = ctx;
        _baselineConfigJson = SerializeConfig(ctx.Config);
        _draftConfig = DeserializeConfig(_baselineConfigJson);
        _lastLocalization = Loc.Current;
        _lastDarkTheme = Theme.IsDark;
        _optionToolTip = new ToolTip
        {
            InitialDelay = 1000,
            ReshowDelay = 1000,
            AutoPopDelay = 20000,
            ShowAlways = true,
        };

        Text = Loc.T("set.title");
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.WindowBack;
        Size = new Size(720, 560);
        MinimumSize = new Size(700, 520);
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.SelectedIndexChanged += OnTabChanged;
        _tabs.MouseMove += OnSettingsTabsMouseMove;
        SetOptionToolTip("tip.tabs", _tabs);

        BuildTabPages();
        _bottomPanel = BuildBottomPanel();
        Controls.Add(_tabs);
        Controls.Add(_bottomPanel);
        _tabs.BringToFront();

        _ctx.SettingsApplied += OnSettingsApplied;
        Activated += (_, _) => RebaseDraftIfLiveChanged(refreshControls: true);

        LoadAllTabs();
    }

    // ================= lifetime =================

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    private void OnOk(object? sender, EventArgs e)
    {
        _finished = true;
        ApplyOwnSettings();
        Close();
    }

    private void OnCancel(object? sender, EventArgs e)
    {
        _finished = true;
        Close();
    }

    private void OnApply(object? sender, EventArgs e)
    {
        ApplyOwnSettings();
    }

    /// <summary>Merges and persists this form's draft, without overwriting unrelated live changes.</summary>
    private void ApplyOwnSettings()
    {
        RebaseDraftIfLiveChanged(refreshControls: false);
        CommitDraftIntoLiveConfig();
        _applying = true;
        try { _ctx.ApplySettings(); }
        finally
        {
            _applying = false;
            ResetDraftFromLiveConfig();
            // Startup registration can reject a setting after SettingsApplied is raised; reload
            // once more from the final live result so the dialog never shows a value that failed.
            if (!IsDisposed && !Disposing) LoadAllTabs();
        }
    }

    private void OnSettingsApplied()
    {
        // Applied from another surface (main window/tray): retain this form's unsaved changes
        // while taking the unrelated live edits as the new merge baseline.
        if (_applying) ResetDraftFromLiveConfig();
        else RebaseDraftIfLiveChanged(refreshControls: false);

        if (_lastLocalization != Loc.Current || _lastDarkTheme != Theme.IsDark)
            RebuildLocalizedUi();
        else
            LoadAllTabs();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        // Closing with X behaves like Cancel; the live config was never changed by this form.
        if (!_finished && e.CloseReason is CloseReason.UserClosing or CloseReason.None)
            _finished = true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ctx.SettingsApplied -= OnSettingsApplied;
        DisposeDiagnosticsTimer();
        _optionToolTip.Dispose();
        base.OnFormClosed(e);
    }

    private static string SerializeConfig(AppConfig config)
        => JsonSerializer.Serialize(config);

    private static AppConfig DeserializeConfig(string json)
    {
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }

    /// <summary>
    /// Rebase the draft on live state with a three-way merge. A draft value identical to its
    /// baseline keeps the live value; unrelated object properties merge independently. Arrays
    /// intentionally edited in both places use the draft order, except named profiles, which
    /// merge by profile name so a tray action in a different profile is retained.
    /// </summary>
    private void RebaseDraftIfLiveChanged(bool refreshControls)
    {
        string liveJson = SerializeConfig(_ctx.Config);
        if (string.Equals(liveJson, _baselineConfigJson, StringComparison.Ordinal)) return;

        var baseline = JsonNode.Parse(_baselineConfigJson);
        var draft = JsonSerializer.SerializeToNode(Config);
        var live = JsonNode.Parse(liveJson);
        var merged = MergeDraftNode(baseline, draft, live, propertyName: null);
        _draftConfig = merged?.Deserialize<AppConfig>() ?? DeserializeConfig(liveJson);
        _baselineConfigJson = liveJson;
        if (refreshControls) LoadAllTabs();
    }

    private void CommitDraftIntoLiveConfig()
    {
        var baseline = JsonNode.Parse(_baselineConfigJson);
        var draft = JsonSerializer.SerializeToNode(Config);
        var live = JsonNode.Parse(SerializeConfig(_ctx.Config));
        var merged = MergeDraftNode(baseline, draft, live, propertyName: null);
        var result = merged?.Deserialize<AppConfig>() ?? Config;
        _ctx.ReplaceConfig(result);
    }

    private void ResetDraftFromLiveConfig()
    {
        _baselineConfigJson = SerializeConfig(_ctx.Config);
        _draftConfig = DeserializeConfig(_baselineConfigJson);
    }

    private static JsonNode? MergeDraftNode(JsonNode? baseline, JsonNode? draft, JsonNode? live, string? propertyName)
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

        // A collection with two concurrent edits has no general item identity. Prefer the draft:
        // applying a list edit must keep its selected order and explicit removals deterministic.
        return draft?.DeepClone();
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

    private void BuildTabPages()
    {
        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(_pageTray = BuildTrayTab());
        _tabs.TabPages.Add(BuildSensorsTab());
        _tabs.TabPages.Add(_pageAlerts = BuildAlertsTab());
        _tabs.TabPages.Add(_pageProfiles = BuildProfilesTab());
        _tabs.TabPages.Add(_pageDiagnostics = BuildDiagnosticsTab());
    }

    /// <summary>Rebuilds labels, controls and delayed help after language or theme changes.</summary>
    private void RebuildLocalizedUi()
    {
        int selectedIndex = Math.Max(0, _tabs.SelectedIndex);
        bool previousLoading = _loading;
        _loading = true;
        try
        {
            foreach (TabPage page in _tabs.TabPages.Cast<TabPage>().ToArray()) page.Dispose();
            _tabs.TabPages.Clear();
            BuildTabPages();

            Controls.Remove(_bottomPanel);
            _bottomPanel.Dispose();
            _bottomPanel = BuildBottomPanel();
            Controls.Add(_bottomPanel);
            _tabs.BringToFront();

            Text = Loc.T("set.title");
            Theme.ApplyTitleBar(this);
            _lastLocalization = Loc.Current;
            _lastDarkTheme = Theme.IsDark;
            _tabs.SelectedIndex = Math.Min(selectedIndex, _tabs.TabCount - 1);
        }
        finally { _loading = previousLoading; }
        LoadAllTabs();
    }

    private void OnTabChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        // These tabs render data that other tabs can change (hidden sensors, thresholds,
        // active profile), so refresh them when they come into view.
        if (_tabs.SelectedTab == _pageTray) LoadTrayTab();
        else if (_tabs.SelectedIndex == 2) LoadSensorsTab();
        else if (_tabs.SelectedTab == _pageAlerts) { LoadAlertsTab(); SeedAlertSoundPicker(); }
        else if (_tabs.SelectedTab == _pageProfiles) LoadProfilesTab();
        else if (_tabs.SelectedTab == _pageDiagnostics) LoadDiagnosticsTab();
    }

    private void OnSettingsTabsMouseMove(object? sender, MouseEventArgs e)
    {
        string key = "tip.tabs";
        for (int i = 0; i < _tabs.TabCount; i++)
        {
            if (!_tabs.GetTabRect(i).Contains(e.Location)) continue;
            key = i switch
            {
                0 => "tip.tab.general",
                1 => "tip.tab.tray",
                2 => "tip.tab.sensors",
                3 => "tip.tab.alerts",
                4 => "tip.tab.profiles",
                5 => "tip.tab.diagnostics",
                _ => "tip.tabs",
            };
            break;
        }
        if (key == _tabTipKey) return;
        _tabTipKey = key;
        SetOptionToolTip(key, _tabs);
    }

    private void LoadAllTabs()
    {
        LoadGeneralTab();
        LoadTrayTab();
        LoadSensorsTab();
        LoadAlertsTab();
        SeedAlertSoundPicker();
        LoadProfilesTab();
        LoadDiagnosticsTab();
    }

    // ================= bottom buttons =================

    private Control BuildBottomPanel()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8, 8, 12, 8),
        };
        var btnApply = new Button { Text = Loc.T("common.apply"), Size = new Size(90, 28) };
        var btnCancel = new Button { Text = Loc.T("common.cancel"), Size = new Size(90, 28) };
        var btnOk = new Button { Text = Loc.T("common.ok"), Size = new Size(90, 28) };
        SetOptionToolTip("tip.common.apply", btnApply);
        SetOptionToolTip("tip.common.cancel", btnCancel);
        SetOptionToolTip("tip.common.ok", btnOk);
        btnApply.Click += OnApply;
        btnCancel.Click += OnCancel;
        btnOk.Click += OnOk;
        flow.Controls.Add(btnApply);   // RightToLeft: first added = rightmost
        flow.Controls.Add(btnCancel);
        flow.Controls.Add(btnOk);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
        return flow;
    }

    // ================= shared helpers =================

    private SensorOverride GetOrCreateOverride(string id)
    {
        if (!Config.SensorOverrides.TryGetValue(id, out var o))
        {
            o = new SensorOverride();
            Config.SensorOverrides[id] = o;
        }
        return o;
    }

    private void PruneOverride(string id)
    {
        // Pinned must keep the override alive — MainForm's favorites live on this flag.
        if (Config.SensorOverrides.TryGetValue(id, out var o)
            && string.IsNullOrEmpty(o.Rename) && !o.Hidden && !o.Pinned && o.Thresholds is null)
        {
            Config.SensorOverrides.Remove(id);
        }
    }

    /// <summary>Drops a Thresholds override that no longer differs from the suggested defaults.</summary>
    private void CleanupAlertOverride(SensorDescriptor d)
    {
        if (!Config.SensorOverrides.TryGetValue(d.Id, out var o) || o.Thresholds is not { } t) return;
        if (t.AlertEnabled || t.PlaySound || !string.IsNullOrEmpty(t.SoundPath)) return;
        var sug = Thresholds.SuggestFor(d.Category, d.Quantity);
        if (t.Yellow == sug.Yellow && t.Red == sug.Red && t.SustainSeconds == sug.SustainSeconds)
        {
            o.Thresholds = null;
            PruneOverride(d.Id);
        }
    }

    private List<SensorDescriptor> SortedDescriptors()
    {
        var list = new List<SensorDescriptor>(_ctx.Sensors.Descriptors);
        list.Sort(static (a, b) =>
        {
            int c = ((int)a.Category).CompareTo((int)b.Category);
            if (c != 0) return c;
            c = string.Compare(a.HardwareName, b.HardwareName, StringComparison.CurrentCultureIgnoreCase);
            if (c != 0) return c;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return list;
    }

    private SensorDescriptor? FindDescriptorById(string id)
    {
        var list = _ctx.Sensors.Descriptors;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == id) return list[i];
        return null;
    }

    private static string CatKey(SensorCategory c) => c switch
    {
        SensorCategory.Cpu => "cat.cpu",
        SensorCategory.Gpu => "cat.gpu",
        SensorCategory.Storage => "cat.storage",
        SensorCategory.Memory => "cat.memory",
        SensorCategory.Battery => "cat.battery",
        SensorCategory.Fan => "cat.fan",
        SensorCategory.Motherboard => "cat.motherboard",
        _ => "cat.other",
    };

    /// <summary>True when the filter is empty or the hardware name / display name contains it.</summary>
    private bool MatchesFilter(SensorDescriptor d, string filter)
    {
        if (filter.Length == 0) return true;
        return d.HardwareName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Config.DisplayNameFor(d).Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static TabPage NewTabPage(string text)
    {
        var page = new TabPage(text);
        // Only override in dark mode; light mode keeps the visual-style tab background.
        if (Theme.IsDark) page.BackColor = Theme.WindowBack;
        return page;
    }

    /// <summary>Adds delayed hover help and the same text for accessibility clients.</summary>
    private void SetOptionToolTip(string key, params Control[] controls)
    {
        string text = Loc.T(key);
        foreach (var control in controls)
        {
            control.AccessibleDescription = text;
            _optionToolTip.SetToolTip(control, text);
        }
    }

    /// <summary>Dark-mode colors for input-style controls (lists, text boxes, combos, numerics);
    /// light mode keeps the default system rendering untouched.</summary>
    private static void ApplyInputTheme(Control control)
    {
        if (!Theme.IsDark) return;
        control.BackColor = Theme.ListBack;
        control.ForeColor = Theme.Text;
    }

    private static TextBox NewFilterBox()
    {
        var box = new TextBox { Dock = DockStyle.Top, PlaceholderText = Loc.T("set.filter") };
        ApplyInputTheme(box);
        return box;
    }

    private static Label NewLabel(string text, int x, int y)
        => new() { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Theme.Text };

    private static ComboBox NewCombo(int x, int y, int width)
    {
        var combo = new ComboBox { Location = new Point(x, y), Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
        ApplyInputTheme(combo);
        return combo;
    }

    private static CheckBox NewCheck(string text, int x, int y)
        => new() { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Theme.Text };

    private static Button NewButton(string text, int x, int y, int width)
        => new() { Text = text, Location = new Point(x, y), Size = new Size(width, 28) };

    private static NumericUpDown NewNumeric(int x, int y, int width, int min, int max)
    {
        var num = new NumericUpDown { Location = new Point(x, y), Width = width, Minimum = min, Maximum = max };
        ApplyInputTheme(num);
        return num;
    }

    private DataGridView NewGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,   // set once (perf)
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            BackgroundColor = Theme.ListBack,
            BorderStyle = BorderStyle.None,
        };
        grid.DefaultCellStyle.BackColor = Theme.ListBack;
        grid.DefaultCellStyle.ForeColor = Theme.Text;
        if (Theme.IsDark)
        {
            // Header visual styles are light-only; swap to themed flat headers in dark mode.
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
            grid.GridColor = Theme.GridLine;
        }
        // Commit checkbox toggles immediately so CellValueChanged fires on click.
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
            e.Cancel = true;
        };
        return grid;
    }
}
