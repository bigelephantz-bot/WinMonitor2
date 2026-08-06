using System.Globalization;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;
using static WinMonitor.UI.ChartControl;   // brings ChartSeriesSource into scope if nested

namespace WinMonitor.UI;

/// <summary>
/// Main monitoring window: grouped sensor list (Current/Min/Max/Avg), history chart of the
/// checked rows, and profile / poll-interval quick controls. Snapshots arrive on the sensor
/// thread and are coalesced onto the UI thread latest-wins, so a slow paint never queues up.
/// </summary>
public sealed class MainForm : Form
{
    // Series colors picked for legibility on the chart background (works on light and dark).
    private static readonly Color[] ChartPalette =
    {
        Color.FromArgb(198, 40, 40),    // #C62828 red
        Color.FromArgb(21, 101, 192),   // #1565C0 blue
        Color.FromArgb(46, 125, 50),    // #2E7D32 green
        Color.FromArgb(230, 81, 0),     // #E65100 orange
        Color.FromArgb(106, 27, 154),   // #6A1B9A purple
        Color.FromArgb(0, 131, 143),    // #00838F teal
        Color.FromArgb(173, 20, 87),    // #AD1457 magenta
        Color.FromArgb(78, 52, 46),     // #4E342E brown
        Color.FromArgb(55, 71, 79),     // #37474F blue-gray
        Color.FromArgb(158, 157, 36),   // #9E9D24 olive
    };

    private static readonly int[] PollValues = { 1000, 2000, 5000 };
    private static readonly string[] PollKeys = { "main.poll.1s", "main.poll.2s", "main.poll.5s" };
    private static readonly int[] ChartMinuteOptions = { 1, 3, 5, 10, 20, 30, 60 };
    private const int StaticZoneSampleCount = 60;
    private const float StaticZoneTolerance = 0.05f;

    private readonly WinMonitorContext _ctx;

    // ---- controls ----
    private readonly MenuStrip _menu;
    private readonly ToolStripMenuItem _fileMenu, _viewMenu, _helpMenu;
    private readonly ToolStripMenuItem _exportCsvItem, _settingsItem, _exitItem;
    private readonly ToolStripMenuItem _copyCardItem, _saveCardItem;
    private readonly ToolStripMenuItem _compactItem, _resetPeaksItem, _rescanItem, _fahrenheitItem, _showChartItem, _showCoresItem, _aboutItem;
    private readonly ContextMenuStrip _rowMenu;
    private readonly ToolStripMenuItem _ctxTrayItem, _ctxPinItem, _ctxHideItem;
    private readonly Panel _banner;
    private readonly Label _bannerLabel;
    private readonly Button _bannerClose;
    private readonly SplitContainer _split;
    private readonly BufferedListView _list;
    private readonly ColumnHeader _colSensor, _colCurrent, _colMin, _colMax, _colAvg;
    private readonly ColumnHeader[] _columns;              // index-aligned with _sortColumn
    private readonly string[] _columnBaseTitles = new string[5];   // titles without sort arrows
    private readonly ChartControl _chart;
    private readonly FlowLayoutPanel _chartBar;
    private readonly Label _chartMinutesLabel;
    private readonly ComboBox _chartMinutesCombo;
    private readonly Label _profileLabel, _pollLabel;
    private readonly ComboBox _profileCombo, _pollCombo;
    private readonly Button _resetButton, _exportButton, _settingsButton;
    private readonly Label _statusLabel;

    // ---- state ----
    private readonly Dictionary<string, SensorRow> _rowsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Thresholds> _thresholdsById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _temperatureIds = new(StringComparer.Ordinal);
    private readonly List<ChartSeriesSource> _chartSources = new();
    private readonly Func<string, long, HistoryReadResult> _displayHistoryProvider;
    private readonly Action _processSnapshotsAction;   // cached: no delegate alloc per poll tick

    private SensorSnapshot[]? _pendingSnapshots;   // latest-wins mailbox, written on the sensor thread
    private int _uiUpdateQueued;                   // 0/1 coalescing flag (Interlocked)
    private long _lastChartRefreshTick;
    private bool _updatingUi;                      // suppresses control events during programmatic updates
    private SensorDescriptor? _rowMenuDescriptor;  // row the context menu was opened on
    private RowComparer? _sorter;                  // active column-sort comparer (null until a header is clicked)
    private int _sortColumn = -1;                  // last column sorted by; -1 = unsorted
    private bool _sortDescending;                  // toggles on repeat clicks of the same column
    private FormWindowState _lastWindowState = FormWindowState.Normal;   // detects minimize/restore in OnResize
    private bool _shownOnce;                        // true after OnLoad: gates out check events fired during handle creation
    private bool _bannerDismissed;
    private bool _backendWarningInitialized;
    private bool _lastBackendWarning;
    private bool _lastCpuTelemetryUnavailable;
    private System.Drawing.Icon? _windowIcon;      // exe icon we extracted and own; disposed to avoid an HICON leak per recreate

    public MainForm(WinMonitorContext ctx)
    {
        _ctx = ctx;
        _displayHistoryProvider = ProvideDisplayHistory;
        _processSnapshotsAction = ProcessPendingSnapshots;

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        // Window/taskbar icon from the exe's embedded ApplicationIcon; cosmetic, failures ignored.
        try { Icon = _windowIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // ---------- menu ----------
        _exportCsvItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ExportTimeSeriesCsv());
        _copyCardItem = new ToolStripMenuItem(string.Empty, null, (_, _) => CopyStatusCard());
        _saveCardItem = new ToolStripMenuItem(string.Empty, null, (_, _) => SaveStatusCard());
        _settingsItem = new ToolStripMenuItem(string.Empty, null, (_, _) => _ctx.ShowSettings());
        // ExitApp persists the config right away, so capture bounds before it runs.
        _exitItem = new ToolStripMenuItem(string.Empty, null, (_, _) => { SaveWindowBounds(); _ctx.ExitApp(); });
        _fileMenu = new ToolStripMenuItem();
        _fileMenu.DropDownItems.Add(_exportCsvItem);
        _fileMenu.DropDownItems.Add(new ToolStripSeparator());
        _fileMenu.DropDownItems.Add(_copyCardItem);
        _fileMenu.DropDownItems.Add(_saveCardItem);
        _fileMenu.DropDownItems.Add(new ToolStripSeparator());
        _fileMenu.DropDownItems.Add(_settingsItem);
        _fileMenu.DropDownItems.Add(new ToolStripSeparator());
        _fileMenu.DropDownItems.Add(_exitItem);

        _compactItem = new ToolStripMenuItem(string.Empty, null, (_, _) => _ctx.ShowCompact());
        _resetPeaksItem = new ToolStripMenuItem(string.Empty, null, (_, _) => _ctx.ResetPeaks());
        // F5 lives on the menu item (not OnKeyDown) so the shortcut is discoverable and fires once.
        _rescanItem = new ToolStripMenuItem(string.Empty, null, (_, _) => { try { _ctx.Sensors.RescanHardware(); } catch { } })
        {
            ShortcutKeys = Keys.F5,
            ShowShortcutKeys = true,
        };
        _fahrenheitItem = new ToolStripMenuItem { CheckOnClick = true };
        _fahrenheitItem.CheckedChanged += (_, _) =>
        {
            if (_updatingUi) return;
            _ctx.Config.UseFahrenheit = _fahrenheitItem.Checked;
            _ctx.ApplySettings();
        };
        _showChartItem = new ToolStripMenuItem { CheckOnClick = true, Checked = true };
        _showChartItem.CheckedChanged += (_, _) =>
        {
            if (_split is not null) _split.Panel2Collapsed = !_showChartItem.Checked;
        };
        // Pure list-visibility toggle: no tray rebuild, so persist + rebuild rows directly
        // rather than going through the heavier ApplySettings path _fahrenheitItem uses.
        _showCoresItem = new ToolStripMenuItem { CheckOnClick = true };
        _showCoresItem.CheckedChanged += (_, _) =>
        {
            if (_updatingUi) return;
            _ctx.Config.ShowPerCoreTemps = _showCoresItem.Checked;
            ConfigStore.Save(_ctx.Config);
            ReloadSensors();
        };
        _viewMenu = new ToolStripMenuItem();
        _viewMenu.DropDownItems.Add(_compactItem);
        _viewMenu.DropDownItems.Add(_resetPeaksItem);
        _viewMenu.DropDownItems.Add(_rescanItem);
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());
        _viewMenu.DropDownItems.Add(_fahrenheitItem);
        _viewMenu.DropDownItems.Add(_showChartItem);
        _viewMenu.DropDownItems.Add(_showCoresItem);

        _aboutItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ShowAbout());
        _helpMenu = new ToolStripMenuItem();
        _helpMenu.DropDownItems.Add(_aboutItem);

        _menu = new MenuStrip();
        _menu.Items.AddRange(new ToolStripItem[] { _fileMenu, _viewMenu, _helpMenu });

        // ---------- elevation warning banner ----------
        _bannerLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        _bannerClose = new Button
        {
            Dock = DockStyle.Right,
            Width = 26,
            FlatStyle = FlatStyle.Flat,
            Text = "×",
            TabStop = false,
        };
        _bannerClose.FlatAppearance.BorderSize = 0;
        _banner = new Panel
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(8, 0, 0, 0),
            Visible = false,
        };
        _bannerClose.Click += (_, _) =>
        {
            _bannerDismissed = true;
            _banner.Visible = false;
        };
        _banner.Controls.Add(_bannerLabel);
        _banner.Controls.Add(_bannerClose);

        // ---------- sensor list ----------
        _colSensor = new ColumnHeader { Width = 250 };
        _colCurrent = new ColumnHeader { Width = 90, TextAlign = HorizontalAlignment.Right };
        _colMin = new ColumnHeader { Width = 80, TextAlign = HorizontalAlignment.Right };
        _colMax = new ColumnHeader { Width = 80, TextAlign = HorizontalAlignment.Right };
        _colAvg = new ColumnHeader { Width = 80, TextAlign = HorizontalAlignment.Right };
        _list = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            ShowGroups = true,
            CheckBoxes = true,                  // checked rows = chart series
            AllowColumnReorder = true,          // drag headers to reorder columns
            HeaderStyle = ColumnHeaderStyle.Clickable,   // click a header to sort within groups
        };
        _list.Columns.AddRange(new[] { _colSensor, _colCurrent, _colMin, _colMax, _colAvg });
        _columns = new[] { _colSensor, _colCurrent, _colMin, _colMax, _colAvg };
        _list.ItemChecked += OnItemChecked;
        _list.ColumnClick += OnColumnClick;

        // ---------- row context menu (tray / favorites / hide) ----------
        _ctxTrayItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ToggleRowTrayIcon());
        _ctxPinItem = new ToolStripMenuItem(string.Empty, null, (_, _) => ToggleRowPinned());
        _ctxHideItem = new ToolStripMenuItem(string.Empty, null, (_, _) => HideRowSensor());
        _rowMenu = new ContextMenuStrip();
        _rowMenu.Items.AddRange(new ToolStripItem[]
        {
            _ctxTrayItem, _ctxPinItem, new ToolStripSeparator(), _ctxHideItem,
        });
        _rowMenu.Opening += OnRowMenuOpening;
        _list.ContextMenuStrip = _rowMenu;

        // ---------- chart panel ----------
        _chart = new ChartControl { Dock = DockStyle.Fill };
        _chartMinutesLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(6, 6, 4, 0),
        };
        _chartMinutesCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 64,
            Margin = new Padding(0, 2, 0, 0),
        };
        _chartMinutesCombo.SelectedIndexChanged += OnChartMinutesChanged;
        _chartBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 28,
            WrapContents = false,
            Padding = new Padding(2, 0, 0, 0),
        };
        _chartBar.Controls.Add(_chartMinutesLabel);
        _chartBar.Controls.Add(_chartMinutesCombo);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 5,
            Panel1MinSize = 120,
            Panel2MinSize = 90,
        };
        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_chart);
        _split.Panel2.Controls.Add(_chartBar);   // added last => docks first (Top)

        // ---------- bottom bar ----------
        _profileLabel = new Label { AutoSize = true, Margin = new Padding(6, 9, 4, 0) };
        _profileCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            Margin = new Padding(0, 5, 0, 0),
        };
        _profileCombo.SelectedIndexChanged += OnProfileChanged;
        _pollLabel = new Label { AutoSize = true, Margin = new Padding(12, 9, 4, 0) };
        _pollCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            Margin = new Padding(0, 5, 0, 0),
        };
        _pollCombo.SelectedIndexChanged += OnPollChanged;
        _resetButton = new Button { AutoSize = true, Margin = new Padding(12, 4, 0, 4) };
        _resetButton.Click += (_, _) => _ctx.ResetPeaks();
        _exportButton = new Button { AutoSize = true, Margin = new Padding(6, 4, 0, 4) };
        _exportButton.Click += (_, _) => ExportTimeSeriesCsv();
        _settingsButton = new Button { AutoSize = true, Margin = new Padding(6, 4, 0, 4) };
        _settingsButton.Click += (_, _) => _ctx.ShowSettings();
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        flow.Controls.Add(_profileLabel);
        flow.Controls.Add(_profileCombo);
        flow.Controls.Add(_pollLabel);
        flow.Controls.Add(_pollCombo);
        flow.Controls.Add(_resetButton);
        flow.Controls.Add(_exportButton);
        flow.Controls.Add(_settingsButton);
        _statusLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 0, 10, 0),
        };
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        bottom.Controls.Add(flow, 0, 0);
        bottom.Controls.Add(_statusLabel, 1, 0);

        // Docking: last added docks first, so add fill first and the menu last.
        Controls.Add(_split);
        Controls.Add(bottom);
        Controls.Add(_banner);
        Controls.Add(_menu);
        MainMenuStrip = _menu;
        ResumeLayout(false);

        // Closing to tray disposes the form (recreated on demand); smart polling must re-evaluate.
        FormClosed += (_, _) => _ctx.UpdateActiveSensorSet();

        RestoreWindowBounds();
        ReloadSensors();   // also applies theme + texts
    }

    // =========================================================================
    // Public surface used by WinMonitorContext
    // =========================================================================

    /// <summary>
    /// SENSOR THREAD. Stores the newest snapshot array and queues at most one UI update;
    /// intermediate ticks are dropped (latest-wins) so the UI never falls behind.
    /// </summary>
    public void AcceptSnapshots(SensorSnapshot[] snapshots)
    {
        Volatile.Write(ref _pendingSnapshots, snapshots);
        if (Interlocked.CompareExchange(ref _uiUpdateQueued, 1, 0) != 0)
            return;
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(_processSnapshotsAction);
            else
                Volatile.Write(ref _uiUpdateQueued, 0);
        }
        // ObjectDisposedException derives from InvalidOperationException; one catch covers both.
        catch (InvalidOperationException) { Volatile.Write(ref _uiUpdateQueued, 0); }
    }

    /// <summary>UI THREAD. Rebuilds all rows from descriptors + config (rename/hide/thresholds).</summary>
    public void ReloadSensors()
    {
        if (IsDisposed) return;
        ListViewportState previousView = CaptureListViewportState();
        _updatingUi = true;
        try
        {
            ApplyTheme();
            ApplyTexts();
            PopulateProfileCombo();
            PopulatePollCombo();
            PopulateChartMinutesCombo();
            _fahrenheitItem.Checked = _ctx.Config.UseFahrenheit;
            _showCoresItem.Checked = _ctx.Config.ShowPerCoreTemps;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                _list.Groups.Clear();
                _rowsById.Clear();
                _thresholdsById.Clear();
                _temperatureIds.Clear();

                var groups = new ListViewGroup[7];
                groups[0] = new ListViewGroup(Loc.T("cat.cpu"));
                groups[1] = new ListViewGroup(Loc.T("cat.gpu"));
                groups[2] = new ListViewGroup(Loc.T("cat.storage"));   // fallback: drives without a model name
                groups[3] = new ListViewGroup(Loc.T("cat.battery"));
                groups[4] = new ListViewGroup(Loc.T("cat.fan"));
                groups[5] = new ListViewGroup(Loc.T("cat.motherboard"));
                groups[6] = new ListViewGroup(Loc.T("cat.other"));

                var descriptors = _ctx.Sensors.Descriptors;

                // Favorites first; the ListView never renders empty groups, so it only shows
                // when at least one visible sensor is pinned.
                var favoritesGroup = new ListViewGroup(Loc.T("main.group.favorites"));
                _list.Groups.Add(favoritesGroup);

                // One group per storage hardware, headed by the model name, so multiple SSDs
                // stay visually separate. Inserted where the single Storage group used to sit
                // (after CPU/GPU); the ListView never renders empty groups, so the generic
                // Storage fallback only shows for drives that report no model name.
                var storageGroups = new Dictionary<string, ListViewGroup>(StringComparer.Ordinal);
                _list.Groups.Add(groups[0]);
                _list.Groups.Add(groups[1]);
                for (int i = 0; i < descriptors.Count; i++)
                {
                    var d = descriptors[i];
                    if (d.Category != SensorCategory.Storage || string.IsNullOrEmpty(d.HardwareName)) continue;
                    if (!IsDisplayedQuantity(d.Quantity) || _ctx.Config.IsHidden(d.Id)) continue;
                    if (!storageGroups.ContainsKey(d.HardwareName))
                    {
                        var g = new ListViewGroup(d.HardwareName);
                        storageGroups.Add(d.HardwareName, g);
                        _list.Groups.Add(g);
                    }
                }
                for (int i = 2; i < groups.Length; i++)
                    _list.Groups.Add(groups[i]);

                bool anyFanRows = false;
                bool hasAggregateCpuTemp = HasVisibleAggregateCpuTemperature(descriptors);
                for (int i = 0; i < descriptors.Count; i++)
                {
                    var d = descriptors[i];
                    if (!IsDisplayedQuantity(d.Quantity)) continue;
                    if (_ctx.Config.IsHidden(d.Id)) continue;
                    // Per-core CPU temps clutter the list (~12 rows on the i7-1360P); collapse them
                    // to Package + Max/Average unless the user opts in via the View menu.
                    if (!_ctx.Config.ShowPerCoreTemps && hasAggregateCpuTemp && IsPerCoreCpuTemp(d)) continue;

                    string name = string.IsNullOrEmpty(d.DisplayName) ? d.Name : d.DisplayName;
                    ListViewGroup group;
                    if (IsPinned(d.Id))
                    {
                        // Pinned rows live in Favorites INSTEAD of their category group,
                        // so every sensor id still maps to exactly one row.
                        group = favoritesGroup;
                    }
                    else if (d.Category == SensorCategory.Storage && storageGroups.TryGetValue(d.HardwareName, out var sg))
                    {
                        group = sg;
                        // The default storage display name is "<model> <sensor>"; the group
                        // header already shows the model, so drop the redundant prefix here.
                        if (name.Length > d.HardwareName.Length
                            && name.StartsWith(d.HardwareName, StringComparison.Ordinal))
                        {
                            string stripped = name.Substring(d.HardwareName.Length).TrimStart();
                            if (stripped.Length > 0) name = stripped;
                        }
                    }
                    else
                    {
                        group = groups[GroupIndexOf(d.Category)];
                    }
                    var item = new ListViewItem(new[] { name, "—", "—", "—", "—" }, group)
                    {
                        Tag = d,
                    };
                    _list.Items.Add(item);
                    _rowsById[d.Id] = new SensorRow(
                        item,
                        d.Id == WellKnown.ThrottleSensorId,
                        d.Id.StartsWith("/wmi/thermalzone/", StringComparison.OrdinalIgnoreCase));
                    // Resolve once per rebuild — ResolveThresholds allocates, so keep it off the poll tick.
                    _thresholdsById[d.Id] = _ctx.Config.ResolveThresholds(d);

                    if (d.Quantity == SensorQuantity.Temperature)
                        _temperatureIds.Add(d.Id);
                    if (d.Category == SensorCategory.Fan) anyFanRows = true;
                }

                if (!anyFanRows) AddInfoRow(groups[4], Loc.T("main.no_fans"));

                MakeGroupsCollapsible();
            }
            finally
            {
                _list.EndUpdate();
            }

            // Restore chart checkboxes from config; with nothing persisted, default to the
            // first non-hidden CPU temperature (never the synthetic throttle row — it's Level).
            var chartIds = _ctx.Config.ChartSensorIds;
            if (chartIds.Count > 0)
            {
                for (int i = 0; i < chartIds.Count; i++)
                    if (_rowsById.TryGetValue(chartIds[i], out var row))
                        row.Item.Checked = true;
            }
            else if (FindDefaultChartDescriptor() is { } def && _rowsById.TryGetValue(def.Id, out var defRow))
            {
                defRow.Item.Checked = true;
            }
            RestoreListViewportState(previousView);
        }
        finally
        {
            _updatingUi = false;
        }
        UpdateChartSources();
    }

    /// <summary>
    /// Structural reloads are intentionally rare; the normal snapshot path updates cells in
    /// place. When a rebuild is necessary, preserve the user's reading position and selection
    /// rather than making an unrelated settings change jump the list back to the top.
    /// </summary>
    private ListViewportState CaptureListViewportState()
    {
        string? topSensorId = SensorIdOf(_list.TopItem);
        string? focusedSensorId = SensorIdOf(_list.FocusedItem);
        var selectedSensorIds = new List<string>(_list.SelectedItems.Count);
        foreach (ListViewItem item in _list.SelectedItems)
        {
            string? id = SensorIdOf(item);
            if (id is not null) selectedSensorIds.Add(id);
        }
        return new ListViewportState(topSensorId, focusedSensorId, selectedSensorIds);
    }

    private void RestoreListViewportState(ListViewportState state)
    {
        for (int i = 0; i < state.SelectedSensorIds.Count; i++)
        {
            if (_rowsById.TryGetValue(state.SelectedSensorIds[i], out var row))
                row.Item.Selected = true;
        }

        if (state.FocusedSensorId is not null && _rowsById.TryGetValue(state.FocusedSensorId, out var focused))
            focused.Item.Focused = true;
        if (state.TopSensorId is not null && _rowsById.TryGetValue(state.TopSensorId, out var top))
            top.Item.EnsureVisible();
    }

    private static string? SensorIdOf(ListViewItem? item)
        => item?.Tag is SensorDescriptor descriptor ? descriptor.Id : null;

    private readonly record struct ListViewportState(
        string? TopSensorId,
        string? FocusedSensorId,
        List<string> SelectedSensorIds);

    // =========================================================================
    // Snapshot -> rows (UI thread, hot path: no LINQ, minimal allocations)
    // =========================================================================

    private void ProcessPendingSnapshots()
    {
        Volatile.Write(ref _uiUpdateQueued, 0);
        if (IsDisposed || !Visible || WindowState == FormWindowState.Minimized) return;
        RefreshBackendWarning();
        var snaps = Volatile.Read(ref _pendingSnapshots);
        if (snaps is null) return;

        // No BeginUpdate/EndUpdate: the pair forces a full-control repaint every tick even when
        // nothing changed. Unchanged raw values skip the cell entirely, so only cells that really
        // change invalidate, and those coalesce into a single WM_PAINT.
        for (int i = 0; i < snaps.Length; i++)
        {
            var s = snaps[i];
            if (!_rowsById.TryGetValue(s.Id, out var row) || row.Item.Tag is not SensorDescriptor d)
                continue;
            var item = row.Item;

            float value = s.Value ?? float.NaN;   // NaN sentinel: Units.Format renders "—"
            if (!value.Equals(row.LastValue))   // float.Equals, not ==: NaN must match NaN
            {
                row.LastValue = value;
                if (row.IsThrottle)
                {
                    // Synthetic throttle state is binary: display True/False, never 0/1 or %.
                    bool active = value >= 0.5f;
                    SetSubItemText(item, 1, float.IsNaN(value)
                        ? "—" : Loc.T(active ? "boolean.true" : "boolean.false"));
                    Color stateColor = float.IsNaN(value) ? _list.ForeColor : active ? Theme.Hot : Theme.Good;
                    if (item.ForeColor != stateColor) item.ForeColor = stateColor;
                }
                else
                {
                    SetSubItemText(item, 1, Units.Format(d.Quantity, value));
                    Color rowColor = float.IsNaN(value) ? _list.ForeColor : RowColorFor(d.Id, value);
                    if (item.ForeColor != rowColor) item.ForeColor = rowColor;
                }
            }

            if (row.IsThrottle) continue;   // min/max/avg stay "—" for the throttle state row

            var stats = _ctx.Stats.GetStats(s.Id);
            float min = float.NaN, max = float.NaN, avg = float.NaN;
            if (stats is { HasData: true }) { min = stats.Min; max = stats.Max; avg = stats.Avg; }
            if (!min.Equals(row.LastMin)) { row.LastMin = min; SetSubItemText(item, 2, Units.Format(d.Quantity, min)); }
            if (!max.Equals(row.LastMax)) { row.LastMax = max; SetSubItemText(item, 3, Units.Format(d.Quantity, max)); }
            if (!avg.Equals(row.LastAvg)) { row.LastAvg = avg; SetSubItemText(item, 4, Units.Format(d.Quantity, avg)); }

            UpdateStaticZoneLabel(row, d, value);
        }

        if (!_split.Panel2Collapsed)
        {
            long tick = Environment.TickCount64;
            if (tick - _lastChartRefreshTick >= 1000)   // throttle chart repaint to ≥1 s
            {
                _lastChartRefreshTick = tick;
                _chart.RefreshData();
            }
        }
    }

    private static void SetSubItemText(ListViewItem item, int index, string text)
    {
        var sub = item.SubItems[index];
        if (!string.Equals(sub.Text, text, StringComparison.Ordinal))
            sub.Text = text;
    }

    private Color RowColorFor(string sensorId, float value)
    {
        if (_thresholdsById.TryGetValue(sensorId, out var t))
        {
            if (value >= t.Red) return Theme.Hot;
            if (value >= t.Yellow) return Theme.Warn;
        }
        return _list.ForeColor;
    }

    /// <summary>
    /// Only ACPI thermal-zone values are candidates for firmware-placeholder labeling. A rolling
    /// window allows a previously fixed zone to recover instead of carrying a lifetime latch.
    /// </summary>
    private void UpdateStaticZoneLabel(SensorRow row, SensorDescriptor descriptor, float value)
    {
        RingBuffer<float>? samples = row.StaticSamples;
        if (samples is null) return;

        if (!_ctx.Config.FlagStaticZones || !float.IsFinite(value))
        {
            samples.Clear();
            SetStaticZoneLabel(row, descriptor, value, isStatic: false);
            return;
        }

        samples.Add(value);
        if (samples.Count < StaticZoneSampleCount)
        {
            SetStaticZoneLabel(row, descriptor, value, isStatic: false);
            return;
        }

        float min = float.MaxValue;
        float max = float.MinValue;
        for (int i = 0; i < samples.Count; i++)
        {
            float sample = samples[i];
            if (sample < min) min = sample;
            if (sample > max) max = sample;
        }
        SetStaticZoneLabel(row, descriptor, value, max - min <= StaticZoneTolerance);
    }

    private void SetStaticZoneLabel(SensorRow row, SensorDescriptor descriptor, float value, bool isStatic)
    {
        if (row.IsStaticLabeled != isStatic)
        {
            row.IsStaticLabeled = isStatic;
            row.Item.Text = isStatic
                ? row.BaseText + " " + Loc.T("main.fixed_value")
                : row.BaseText;
        }

        Color color = isStatic
            ? Theme.SubtleText
            : float.IsFinite(value) ? RowColorFor(descriptor.Id, value) : _list.ForeColor;
        if (row.Item.ForeColor != color) row.Item.ForeColor = color;
    }

    /// <summary>
    /// Row handle plus the raw values last written to it, so unchanged ticks skip formatting
    /// and cell writes entirely. NaN doubles as the "no value" sentinel and matches the "—"
    /// placeholder the row is created with. Rebuilt (cache reset) by ReloadSensors.
    /// </summary>
    private sealed class SensorRow
    {
        public readonly ListViewItem Item;
        public readonly bool IsThrottle;   // synthetic throttle state row: word cell, no min/max/avg
        public readonly string BaseText;
        public readonly RingBuffer<float>? StaticSamples;
        public float LastValue = float.NaN;
        public float LastMin = float.NaN;
        public float LastMax = float.NaN;
        public float LastAvg = float.NaN;
        public bool IsStaticLabeled;
        public SensorRow(ListViewItem item, bool isThrottle, bool trackStaticZone)
        {
            Item = item;
            IsThrottle = isThrottle;
            BaseText = item.Text;
            if (trackStaticZone) StaticSamples = new RingBuffer<float>(StaticZoneSampleCount);
        }
    }

    /// <summary>
    /// Column-click sorter. Text column compares the Sensor display text; value columns parse
    /// the leading number out of the formatted cell (e.g. "45°C", "3400 RPM", "12.5 W") and
    /// compare as float. Empty and "—" placeholder cells always sort last, both directions.
    /// Reused across clicks (only its fields change) so no per-sort allocation.
    /// </summary>
    private sealed class RowComparer : System.Collections.IComparer
    {
        public int Column;
        public bool Descending;
        public bool TextColumn;
        public string? FixedSuffix;   // " (fixed)" — stripped before comparing so the label can't reorder rows

        public int Compare(object? x, object? y)
        {
            var a = (ListViewItem?)x;
            var b = (ListViewItem?)y;
            if (a is null || b is null) return 0;

            if (TextColumn)
            {
                int cmp = string.Compare(StripFixed(a.Text), StripFixed(b.Text), StringComparison.CurrentCulture);
                return Descending ? -cmp : cmp;
            }

            string sa = Column < a.SubItems.Count ? a.SubItems[Column].Text : "";
            string sb = Column < b.SubItems.Count ? b.SubItems[Column].Text : "";
            bool va = TryParseValue(sa, out float fa);
            bool vb = TryParseValue(sb, out float fb);
            // Missing values ("—"/empty) sink to the bottom regardless of sort direction.
            if (!va && !vb) return 0;
            if (!va) return 1;
            if (!vb) return -1;
            int c = fa.CompareTo(fb);
            return Descending ? -c : c;
        }

        /// <summary>Removes a trailing " (fixed)" static-zone marker so it never affects sort order.</summary>
        private string StripFixed(string text)
        {
            if (!string.IsNullOrEmpty(FixedSuffix) && text.EndsWith(FixedSuffix, StringComparison.Ordinal))
                return text.Substring(0, text.Length - FixedSuffix.Length);
            return text;
        }

        /// <summary>
        /// Parses the leading numeric part of a formatted cell, ignoring a unit suffix such as
        /// "°C", " RPM", " %", " W", " V", " GB". Returns false for the "—" placeholder / blanks.
        /// </summary>
        private static bool TryParseValue(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(text) || text == "—") return false;
            int i = 0;
            int len = text.Length;
            // Allow a leading sign then digits, one decimal point, and more digits.
            if (i < len && (text[i] == '-' || text[i] == '+')) i++;
            int start = i;
            bool seenDot = false;
            while (i < len)
            {
                char c = text[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' && !seenDot) { seenDot = true; i++; continue; }
                break;
            }
            if (i == start || (i == start + 1 && seenDot)) return false;   // no digits captured
            if (!float.TryParse(text.AsSpan(0, i), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value))
                return false;
            // Data cells format as GB/TB and clocks as MHz/GHz. Scale compact units back onto
            // their base units so mixed-unit rows sort numerically.
            if (text.IndexOf("TB", i, StringComparison.Ordinal) >= 0)
                value *= 1024f;
            else if (text.IndexOf("GHz", i, StringComparison.Ordinal) >= 0)
                value *= 1000f;
            return true;
        }
    }

    // =========================================================================
    // Chart wiring
    // =========================================================================

    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        // Fires once per user toggle; programmatic check restore is guarded by _updatingUi.
        // The default-check applied before the form is shown fires later during handle creation
        // (outside _updatingUi), so gate it out too — else auto-selection persists as explicit.
        if (_updatingUi || !_shownOnce) return;
        PersistCheckedChartIds();
        UpdateChartSources();
    }

    /// <summary>Writes the currently checked sensor ids into the config and saves it.</summary>
    private void PersistCheckedChartIds()
    {
        var ids = _ctx.Config.ChartSensorIds;
        ids.Clear();
        foreach (ListViewItem it in _list.CheckedItems)
            if (it.Tag is SensorDescriptor d) ids.Add(d.Id);
        ConfigStore.Save(_ctx.Config);
    }

    /// <summary>
    /// Header click sorts rows within their groups by the clicked column, toggling
    /// ascending/descending on repeat clicks of the same column. Groups keep items partitioned,
    /// so WinForms sorts inside each group when every item has a .Group (all our rows do).
    /// </summary>
    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (_updatingUi) return;
        _updatingUi = true;
        try
        {
            if (e.Column == _sortColumn) _sortDescending = !_sortDescending;
            else { _sortColumn = e.Column; _sortDescending = false; }

            // Sensor column (0) compares display text; value columns parse the numeric part.
            _sorter ??= new RowComparer();
            _sorter.Column = _sortColumn;
            _sorter.Descending = _sortDescending;
            _sorter.TextColumn = _sortColumn == 0;
            _sorter.FixedSuffix = " " + Loc.T("main.fixed_value");
            _list.ListViewItemSorter = _sorter;
            _list.Sort();
            UpdateSortIndicators();
        }
        finally
        {
            _updatingUi = false;
        }
    }

    /// <summary>Shows " ▲"/" ▼" on the sorted column header; the others show their base title.</summary>
    private void UpdateSortIndicators()
    {
        for (int i = 0; i < _columns.Length; i++)
        {
            string title = _columnBaseTitles[i] ?? string.Empty;
            if (i == _sortColumn) title += _sortDescending ? " ▼" : " ▲";
            if (!string.Equals(_columns[i].Text, title, StringComparison.Ordinal))
                _columns[i].Text = title;
        }
    }

    private void UpdateChartSources()
    {
        _chartSources.Clear();
        foreach (ListViewItem it in _list.CheckedItems)
        {
            if (it.Tag is not SensorDescriptor d) continue;
            _chartSources.Add(MakeChartSource(d, ChartPalette[_chartSources.Count % ChartPalette.Length]));
        }
        if (_chartSources.Count == 0)
        {
            var def = FindDefaultChartDescriptor();
            if (def is not null)
                _chartSources.Add(MakeChartSource(def, ChartPalette[0]));
        }
        _chart.SetSources(_chartSources, _displayHistoryProvider, _ctx.Config.ChartMinutes);
    }

    private ChartSeriesSource MakeChartSource(SensorDescriptor d, Color color)
    {
        var t = _ctx.Config.ResolveThresholds(d);
        float yellow = t.Yellow, red = t.Red;
        // The history provider feeds the chart display-unit temperatures (°F when enabled);
        // the bands must be converted identically or the colored segments would shift.
        // float.MaxValue means "never colored" and passes through untouched.
        if (d.Quantity == SensorQuantity.Temperature && Units.UseFahrenheit)
        {
            if (yellow < float.MaxValue) yellow = Units.ToDisplayTemp(yellow);
            if (red < float.MaxValue) red = Units.ToDisplayTemp(red);
        }
        return new ChartSeriesSource(
            d.Id, RowNameOf(d), color, yellow, red, d.Quantity,
            string.Equals(d.Id, WellKnown.ThrottleSensorId, StringComparison.Ordinal));
    }

    private SensorDescriptor? FindDefaultChartDescriptor()
    {
        var list = _ctx.Sensors.Descriptors;
        SensorDescriptor? firstTemp = null;
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            if (d.Quantity != SensorQuantity.Temperature || _ctx.Config.IsHidden(d.Id)) continue;
            if (d.Category == SensorCategory.Cpu) return d;
            firstTemp ??= d;
        }
        return firstTemp;
    }

    /// <summary>
    /// History for the chart. Temperatures are converted to the display unit here so the
    /// chart itself stays unit-agnostic; other quantities are passed through raw.
    /// </summary>
    private HistoryReadResult ProvideDisplayHistory(string id, long knownVersion)
    {
        HistoryReadResult result = _ctx.Stats.GetHistoryIfChanged(id, knownVersion);
        TimedValue[]? history = result.Values;
        if (history is null || !Units.UseFahrenheit || !_temperatureIds.Contains(id) || history.Length == 0)
            return result;
        var converted = new TimedValue[history.Length];
        for (int i = 0; i < history.Length; i++)
            converted[i] = new TimedValue(history[i].Utc, Units.ToDisplayTemp(history[i].Value));
        return new HistoryReadResult(result.Version, converted);
    }

    // =========================================================================
    // Bottom-bar handlers
    // =========================================================================

    private void OnProfileChanged(object? sender, EventArgs e)
    {
        if (_updatingUi || _profileCombo.SelectedItem is not string name) return;
        if (string.Equals(name, _ctx.Config.ActiveProfile, StringComparison.Ordinal)) return;
        _ctx.Config.ActiveProfile = name;
        _ctx.ApplySettings();
    }

    private void OnPollChanged(object? sender, EventArgs e)
    {
        if (_updatingUi) return;
        int idx = _pollCombo.SelectedIndex;
        if (idx < 0 || idx >= PollValues.Length) return;
        if (_ctx.Config.PollIntervalMs == PollValues[idx]) return;
        _ctx.Config.PollIntervalMs = PollValues[idx];
        _ctx.ApplySettings();
    }

    private void OnChartMinutesChanged(object? sender, EventArgs e)
    {
        if (_updatingUi) return;
        int idx = _chartMinutesCombo.SelectedIndex;
        if (idx < 0 || idx >= ChartMinuteOptions.Length) return;
        _ctx.Config.ChartMinutes = ChartMinuteOptions[idx];
        // A chart-window change is local to this form; persist it immediately without routing
        // through the heavier full-settings pipeline (which would rebuild the sensor list).
        ConfigStore.Save(_ctx.Config);
        UpdateChartSources();
    }

    // =========================================================================
    // Row context menu (tray / favorites / hide)
    // =========================================================================

    private void OnRowMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Right-click: resolve the row under the cursor; keyboard menu key: the focused row.
        // HitTest throws outside the client area, so guard the keyboard-invoked case.
        var pos = _list.PointToClient(Cursor.Position);
        var item = _list.ClientRectangle.Contains(pos) ? _list.HitTest(pos).Item : null;
        item ??= _list.FocusedItem;
        _rowMenuDescriptor = item?.Tag as SensorDescriptor;   // info rows have a null Tag

        bool enabled = _rowMenuDescriptor is not null;
        _ctxTrayItem.Enabled = enabled;
        _ctxPinItem.Enabled = enabled;
        _ctxHideItem.Enabled = enabled;
        _ctxTrayItem.Checked = _rowMenuDescriptor is { } d && FindSingleSensorTrayIcon(d.Id) >= 0;
        _ctxPinItem.Checked = _rowMenuDescriptor is { } p && IsPinned(p.Id);
    }

    /// <summary>Index of the active profile's tray icon showing exactly this sensor, or -1.</summary>
    private int FindSingleSensorTrayIcon(string sensorId)
    {
        var icons = _ctx.Config.Active.TrayIcons;
        for (int i = 0; i < icons.Count; i++)
            if (icons[i].SensorIds.Count == 1 && icons[i].SensorIds[0] == sensorId)
                return i;
        return -1;
    }

    private void ToggleRowTrayIcon()
    {
        if (_rowMenuDescriptor is not { } d) return;
        var icons = _ctx.Config.Active.TrayIcons;
        int existing = FindSingleSensorTrayIcon(d.Id);
        if (existing >= 0) icons.RemoveAt(existing);
        else icons.Add(new TrayIconConfig { SensorIds = { d.Id } });
        // This only affects NotifyIcons. Avoid the full settings pipeline because its main
        // list rebuild resets the user's current scroll position and selection.
        _ctx.ApplyTrayIconConfiguration();
    }

    private void ToggleRowPinned()
    {
        if (_rowMenuDescriptor is not { } d) return;
        if (_ctx.Config.SensorOverrides.TryGetValue(d.Id, out var o) && o.Pinned)
        {
            o.Pinned = false;
            PruneOverride(d.Id);
        }
        else
        {
            GetOrCreateOverride(d.Id).Pinned = true;
        }
        // Pure list regrouping: no tray/alert impact, so skip the heavier ApplySettings path.
        ConfigStore.Save(_ctx.Config);
        ReloadSensors();
    }

    private void HideRowSensor()
    {
        if (_rowMenuDescriptor is not { } d) return;
        GetOrCreateOverride(d.Id).Hidden = true;
        _ctx.ApplySettings();
    }

    private bool IsPinned(string sensorId)
        => _ctx.Config.SensorOverrides.TryGetValue(sensorId, out var o) && o.Pinned;

    private SensorOverride GetOrCreateOverride(string id)
    {
        if (!_ctx.Config.SensorOverrides.TryGetValue(id, out var o))
        {
            o = new SensorOverride();
            _ctx.Config.SensorOverrides[id] = o;
        }
        return o;
    }

    /// <summary>Drops an override that carries no information anymore (mirrors SettingsForm).</summary>
    private void PruneOverride(string id)
    {
        if (_ctx.Config.SensorOverrides.TryGetValue(id, out var o)
            && string.IsNullOrEmpty(o.Rename) && !o.Hidden && !o.Pinned && o.Thresholds is null)
        {
            _ctx.Config.SensorOverrides.Remove(id);
        }
    }

    // =========================================================================
    // Exports / About
    // =========================================================================

    private async void ExportTimeSeriesCsv()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = CsvFilter(),
            DefaultExt = "csv",
            FileName = "winmonitor-timeseries-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var descriptors = new SensorDescriptor[_ctx.Sensors.Descriptors.Count];
        for (int i = 0; i < descriptors.Length; i++)
        {
            SensorDescriptor source = _ctx.Sensors.Descriptors[i];
            descriptors[i] = new SensorDescriptor
            {
                Id = source.Id,
                HardwareName = source.HardwareName,
                Name = source.Name,
                DisplayName = source.DisplayName,
                Category = source.Category,
                Quantity = source.Quantity,
            };
        }

        _exportButton.Enabled = false;
        _exportCsvItem.Enabled = false;
        try
        {
            // Raw values keep CSV units stable even if the display uses Fahrenheit. Disk-backed
            // history is streamed on a worker so a long-running session cannot freeze the UI.
            string path = await Task.Run(
                () => _ctx.Stats.ExportTimeSeriesCsv(dlg.FileName, descriptors));
            if (IsDisposed || Disposing) return;
            MessageBox.Show(this, Loc.F("main.export_done", path), Loc.T("app.name"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
                MessageBox.Show(this, ex.Message, Loc.T("common.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                _exportButton.Enabled = true;
                _exportCsvItem.Enabled = true;
            }
        }
    }

    private static string CsvFilter()
    {
        // Guard: if the key is missing Loc falls back to the key itself, which is not a
        // valid WinForms filter string and would make SaveFileDialog throw.
        string filter = Loc.T("file.csv_filter");
        return filter.IndexOf('|') >= 0 ? filter : "CSV (*.csv)|*.csv";
    }

    // =========================================================================
    // Status card (shareable PNG snapshot of the visible rows)
    // =========================================================================

    private void CopyStatusCard()
    {
        try
        {
            using var bmp = RenderStatusCard();
            Clipboard.SetImage(bmp);   // clipboard keeps its own copy; disposing bmp is safe
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("common.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveStatusCard()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png",
            DefaultExt = "png",
            FileName = "winmonitor-status-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var bmp = RenderStatusCard();
            bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("common.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Renders the visible sensor rows into a themed bitmap: header (app name + timestamp),
    /// group headers as section titles, one line per row with the current value colored the
    /// same as the list. User-initiated (menu), so allocations here are fine.
    /// </summary>
    private Bitmap RenderStatusCard()
    {
        const int width = 420, pad = 12, lineH = 17, sectionH = 22, headerH = 26;

        // Collect the visible sensor rows per group; also determines the height.
        var sections = new List<(string Title, List<ListViewItem> Rows)>();
        int height = pad + headerH + pad;
        foreach (ListViewGroup g in _list.Groups)
        {
            List<ListViewItem>? rows = null;
            foreach (ListViewItem it in g.Items)
            {
                if (it.Tag is not SensorDescriptor) continue;   // info rows carry no status
                (rows ??= new List<ListViewItem>()).Add(it);
            }
            if (rows is null) continue;
            sections.Add((g.Header, rows));
            height += sectionH + rows.Count * lineH;
        }

        var bmp = new Bitmap(width, height);
        try
        {
            using var g2 = Graphics.FromImage(bmp);
            using var textBrush = new SolidBrush(Theme.Text);
            using var subtleBrush = new SolidBrush(Theme.SubtleText);
            using var bold = new Font(Font, FontStyle.Bold);
            using var border = new Pen(Theme.Border);
            using var nameFormat = new StringFormat(StringFormatFlags.NoWrap)
            {
                Trimming = StringTrimming.EllipsisCharacter,
            };

            g2.Clear(Theme.WindowBack);
            g2.DrawRectangle(border, 0, 0, width - 1, height - 1);

            float y = pad;
            g2.DrawString(Loc.T("app.name"), bold, textBrush, pad, y);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            SizeF stampSize = g2.MeasureString(stamp, Font);
            g2.DrawString(stamp, Font, subtleBrush, width - pad - stampSize.Width, y + 1f);
            y += headerH;

            foreach (var (title, rows) in sections)
            {
                y += 4f;
                g2.DrawString(title, bold, subtleBrush, pad, y);
                y += sectionH - 4f;
                foreach (var it in rows)
                {
                    g2.DrawString(it.Text, Font, textBrush,
                        new RectangleF(pad, y, 190f, lineH), nameFormat);
                    string current = it.SubItems[1].Text;
                    SizeF cs = g2.MeasureString(current, Font);
                    using (var valueBrush = new SolidBrush(it.ForeColor))   // threshold-colored like the list
                        g2.DrawString(current, Font, valueBrush, 300f - cs.Width, y);
                    string minMax = it.SubItems[2].Text + " / " + it.SubItems[3].Text;
                    SizeF ms = g2.MeasureString(minMax, Font);
                    g2.DrawString(minMax, Font, subtleBrush, width - pad - ms.Width, y);
                    y += lineH;
                }
            }
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(this, Loc.T("app.name") + "\n\n" + Loc.T("main.about_text"), Loc.T("main.about"),
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // =========================================================================
    // UI population helpers
    // =========================================================================

    private void ApplyTexts()
    {
        Text = Loc.T("app.name");
        _fileMenu.Text = WithMnemonic(Loc.T("main.file"), 'F');
        _viewMenu.Text = WithMnemonic(Loc.T("main.view"), 'V');
        _helpMenu.Text = WithMnemonic(Loc.T("main.help"), 'H');
        _exportCsvItem.Text = Loc.T("main.export_csv");
        _copyCardItem.Text = Loc.T("main.statuscard.copy");
        _saveCardItem.Text = Loc.T("main.statuscard.save");
        _settingsItem.Text = WithMnemonic(Loc.T("main.settings"), 'S') + "…";
        _exitItem.Text = WithMnemonic(Loc.T("main.exit"), 'X');
        _compactItem.Text = Loc.T("main.compact");
        _resetPeaksItem.Text = Loc.T("main.reset_peaks");
        _rescanItem.Text = Loc.T("main.rescan");
        _fahrenheitItem.Text = Loc.T("main.use_fahrenheit");
        _showChartItem.Text = Loc.T("main.show_chart");
        _showCoresItem.Text = Loc.T("main.show_cores");
        _aboutItem.Text = Loc.T("main.about") + "…";
        _ctxTrayItem.Text = Loc.T("main.ctx.tray");
        _ctxPinItem.Text = Loc.T("main.ctx.pin");
        _ctxHideItem.Text = Loc.T("main.ctx.hide");
        _columnBaseTitles[0] = Loc.T("main.col.sensor");
        _columnBaseTitles[1] = Loc.T("main.col.value");
        _columnBaseTitles[2] = Loc.T("main.col.min");
        _columnBaseTitles[3] = Loc.T("main.col.max");
        _columnBaseTitles[4] = Loc.T("main.col.avg");
        UpdateSortIndicators();   // writes the column headers, re-adding the current sort arrow
        RefreshBackendWarning(forceText: true);
        _profileLabel.Text = Loc.T("main.profile");
        _pollLabel.Text = Loc.T("main.poll");
        _chartMinutesLabel.Text = Loc.T("main.chart_minutes");
        _resetButton.Text = Loc.T("main.reset_peaks");
        _exportButton.Text = Loc.T("main.export_csv");
        _settingsButton.Text = Loc.T("main.settings") + "…";
        _statusLabel.Text = BuildStatusText();
    }

    /// <summary>
    /// Adds a keyboard mnemonic to a menu text: "&amp;Text" when the text contains a latin
    /// letter, otherwise the CJK convention "Text(&amp;F)" (e.g. "檔案(F)"), so zh-TW menus
    /// stay keyboard-accessible.
    /// </summary>
    private static string WithMnemonic(string text, char mnemonic)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                return "&" + text;
        }
        return text + "(&" + mnemonic + ")";
    }

    /// <summary>Applies the current Theme to every themed surface. Called from ReloadSensors,
    /// so a theme change propagated via ApplySettings restyles the open window in place.</summary>
    private void ApplyTheme()
    {
        BackColor = Theme.WindowBack;
        _list.BackColor = Theme.ListBack;
        _list.ForeColor = Theme.Text;
        _banner.BackColor = Theme.BannerBack;
        _bannerLabel.ForeColor = Theme.BannerText;
        _bannerClose.ForeColor = Theme.BannerText;
        _split.Panel2.BackColor = Theme.Surface;
        _chartBar.BackColor = Theme.Surface;
        _chartMinutesLabel.ForeColor = Theme.SubtleText;
        _statusLabel.ForeColor = Theme.SubtleText;
        Theme.StyleMenu(_menu);
        _rowMenu.BackColor = Theme.Surface;
        _rowMenu.ForeColor = Theme.Text;
        foreach (ToolStripItem item in _rowMenu.Items)
            item.ForeColor = Theme.Text;
        if (IsHandleCreated) Theme.ApplyTitleBar(this);
    }

    private string BuildStatusText()
    {
        string status = _ctx.Sensors.IsElevated ? Loc.T("main.status.elevated") : Loc.T("main.status.not_elevated");
        if (_ctx.Sensors.PawnIoDetected)
            status += " · " + Loc.T("main.status.pawnio")
                + (_ctx.Sensors.PawnIoVersion is { } version ? " " + version.ToString(3) : "");
        if (!_ctx.Sensors.CpuTelemetryAvailable)
            status += " · " + Loc.T("main.status.cpu_unavailable");
        return status;
    }

    private void RefreshBackendWarning(bool forceText = false)
    {
        bool cpuUnavailable = !_ctx.Sensors.CpuTelemetryAvailable;
        bool warning = !_ctx.Sensors.IsElevated || cpuUnavailable;
        bool changed = !_backendWarningInitialized
            || warning != _lastBackendWarning
            || cpuUnavailable != _lastCpuTelemetryUnavailable;

        if (changed)
        {
            // A newly detected failure deserves one visible warning even if an earlier warning
            // was dismissed. A stable dismissed warning stays closed across normal poll ticks.
            if (!_backendWarningInitialized || (warning && !_lastBackendWarning))
                _bannerDismissed = false;
            _backendWarningInitialized = true;
            _lastBackendWarning = warning;
            _lastCpuTelemetryUnavailable = cpuUnavailable;
        }

        if (changed || forceText)
        {
            _bannerLabel.Text = !_ctx.Sensors.IsElevated
                ? Loc.T("main.not_elevated")
                : _ctx.Sensors.PawnIoUpdateRequired
                    ? Loc.F("main.cpu_unavailable_old_pawnio", _ctx.Sensors.PawnIoVersion?.ToString(3) ?? "?")
                    : Loc.T("main.cpu_unavailable");
            _statusLabel.Text = BuildStatusText();
        }
        _banner.Visible = warning && !_bannerDismissed;
    }

    private void PopulateProfileCombo()
    {
        _profileCombo.Items.Clear();
        var profiles = _ctx.Config.Profiles;
        int selected = 0;
        for (int i = 0; i < profiles.Count; i++)
        {
            _profileCombo.Items.Add(profiles[i].Name);
            if (string.Equals(profiles[i].Name, _ctx.Config.ActiveProfile, StringComparison.Ordinal))
                selected = i;
        }
        if (_profileCombo.Items.Count > 0)
            _profileCombo.SelectedIndex = selected;
    }

    private void PopulatePollCombo()
    {
        _pollCombo.Items.Clear();
        for (int i = 0; i < PollKeys.Length; i++)
            _pollCombo.Items.Add(Loc.T(PollKeys[i]));
        int selected = 1;   // 2 s default
        for (int i = 0; i < PollValues.Length; i++)
            if (PollValues[i] == _ctx.Config.PollIntervalMs) { selected = i; break; }
        _pollCombo.SelectedIndex = selected;
    }

    private void PopulateChartMinutesCombo()
    {
        _chartMinutesCombo.Items.Clear();
        int selected = 0;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < ChartMinuteOptions.Length; i++)
        {
            _chartMinutesCombo.Items.Add(ChartMinuteOptions[i].ToString(CultureInfo.InvariantCulture));
            int diff = Math.Abs(ChartMinuteOptions[i] - _ctx.Config.ChartMinutes);
            if (diff < bestDiff) { bestDiff = diff; selected = i; }
        }
        _chartMinutesCombo.SelectedIndex = selected;
    }

    private void AddInfoRow(ListViewGroup group, string text)
    {
        var item = new ListViewItem(new[] { text, "", "", "", "" }, group)
        {
            ForeColor = Theme.SubtleText,
            Tag = null,
        };
        _list.Items.Add(item);
    }

    private void MakeGroupsCollapsible()
    {
        // CollapsedState wraps the LVM_SETGROUPINFO + LVGS_COLLAPSIBLE Win32 call;
        // purely cosmetic, so any failure (old OS, handle quirks) is swallowed.
        try
        {
            foreach (ListViewGroup g in _list.Groups)
                g.CollapsedState = ListViewGroupCollapsedState.Expanded;
        }
        catch { }
    }

    // Memory has no group: SensorService emits no Memory hardware, and anything mapped to the
    // category by an override would land in Other.
    private static int GroupIndexOf(SensorCategory category) => category switch
    {
        SensorCategory.Cpu => 0,
        SensorCategory.Gpu => 1,
        SensorCategory.Storage => 2,
        SensorCategory.Battery => 3,
        SensorCategory.Fan => 4,
        SensorCategory.Motherboard => 5,
        _ => 6,
    };

    /// <summary>
    /// True for an individual CPU core temperature row ("CPU Core #1", "Core #3", …) as opposed
    /// to the aggregates we always keep ("CPU Package", "Core Max", "Core Average", "Core (Tctl…",
    /// "CCD…"). Intel hybrid CPUs use "P-Core #…" and "E-Core #…"; the "#" in each
    /// prefix separates a per-core row from the aggregate readings.
    /// </summary>
    private static bool IsPerCoreCpuTemp(SensorDescriptor d)
    {
        if (d.Category != SensorCategory.Cpu || d.Quantity != SensorQuantity.Temperature)
            return false;
        string n = d.Name;
        return n.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("P-Core #", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("E-Core #", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasVisibleAggregateCpuTemperature(IReadOnlyList<SensorDescriptor> descriptors)
    {
        for (int i = 0; i < descriptors.Count; i++)
        {
            SensorDescriptor d = descriptors[i];
            if (d.Category == SensorCategory.Cpu
                && d.Quantity == SensorQuantity.Temperature
                && !IsPerCoreCpuTemp(d)
                && !_ctx.Config.IsHidden(d.Id))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsDisplayedQuantity(SensorQuantity quantity) => quantity switch
    {
        SensorQuantity.Temperature or SensorQuantity.Fan or SensorQuantity.Control
            or SensorQuantity.Level or SensorQuantity.Power or SensorQuantity.Data
            or SensorQuantity.Voltage or SensorQuantity.Load or SensorQuantity.Frequency => true,
        _ => false,
    };

    private static string RowNameOf(SensorDescriptor d)
        => string.IsNullOrEmpty(d.DisplayName) ? d.Name : d.DisplayName;

    // =========================================================================
    // Window lifecycle
    // =========================================================================

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _shownOnce = true;   // from here on, check events are genuine user toggles
        try { _split.SplitterDistance = Math.Max(_split.Panel1MinSize, _split.Height * 62 / 100); }
        catch { /* cosmetic; SplitContainer throws when too small */ }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == _lastWindowState) return;
        bool wasMinimized = _lastWindowState == FormWindowState.Minimized;
        _lastWindowState = WindowState;
        // Only minimize/restore transitions matter: smart polling treats minimized as hidden.
        if (!wasMinimized && WindowState != FormWindowState.Minimized) return;

        _ctx.UpdateActiveSensorSet();
        if (wasMinimized)
        {
            // Rows and chart went stale while minimized; catch up now instead of on the next tick.
            _lastChartRefreshTick = 0;
            ProcessPendingSnapshots();
        }
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        // Keep config bounds current so an exit from the tray (no FormClosing) still restores.
        SaveWindowBounds();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveWindowBounds();
        if (e.CloseReason == CloseReason.UserClosing)
        {
            if (_ctx.Config.CloseToTray)
            {
                bool minimize = true;
                if (_ctx.Config.ConfirmOnClose)
                {
                    var result = MessageBox.Show(this,
                        Loc.T("main.confirm_close.text"), Loc.T("main.confirm_close.title"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    minimize = result == DialogResult.Yes;
                }
                if (!minimize)
                {
                    _ctx.ExitApp();
                }
                // "Minimize to tray" lets the form close and DISPOSE so a hidden window holds no
                // GDI/handle resources; WinMonitorContext.ShowMainWindow recreates it on demand.
                // Config.ChartSensorIds is already current (OnItemChecked persists real toggles),
                // so no flush here — persisting the auto-default would clobber empty=auto.
            }
            else
            {
                _ctx.ExitApp();
            }
        }
        base.OnFormClosing(e);
    }

    private void RestoreWindowBounds()
    {
        Size = new Size(760, 560);
        if (_ctx.Config.MainWindowBounds is { } saved
            && saved.Width >= 300 && saved.Height >= 200
            && IsUsefullyOnScreen(saved))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = saved;
        }
    }

    private void SaveWindowBounds()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width >= 300 && bounds.Height >= 200)
            _ctx.Config.MainWindowBounds = bounds;
    }

    private static bool IsUsefullyOnScreen(Rectangle bounds)
    {
        foreach (var screen in Screen.AllScreens)
        {
            var overlap = Rectangle.Intersect(screen.WorkingArea, bounds);
            if (overlap.Width >= 120 && overlap.Height >= 40)
                return true;   // enough of the title bar is reachable
        }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        // The context menu is a component, not a child control, so the form's dispose does not
        // reach it — and close-to-tray now disposes this form on every close.
        if (disposing)
        {
            _rowMenu?.Dispose();
            // Only the HICON we extracted (ExtractAssociatedIcon owns a native handle); it leaks
            // per close-to-tray recreate otherwise. Null when extraction failed — then the shared
            // default form icon is in use, which must never be disposed here.
            _windowIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// ListView with double buffering enabled (protected on the base class). Kills the
    /// flicker of per-tick subitem updates on the native control.
    /// </summary>
    private sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;   // maps to LVS_EX_DOUBLEBUFFER on modern WinForms
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }
    }
}
