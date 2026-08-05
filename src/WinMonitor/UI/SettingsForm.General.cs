using System.Globalization;
using WinMonitor.Config;
using WinMonitor.Localization;

namespace WinMonitor.UI;

// SettingsForm — General tab: language/theme/units/polling, startup, throttle,
// auto-reset, logging controls and the restore-defaults button.
public sealed partial class SettingsForm
{
    // General
    private ComboBox _cboLanguage = null!, _cboTheme = null!, _cboUnits = null!, _cboPoll = null!;
    private CheckBox _chkAutostart = null!, _chkStartMin = null!, _chkCloseToTray = null!, _chkConfirmClose = null!, _chkLogging = null!;
    private CheckBox _chkBatteryAdaptive = null!, _chkShowCores = null!, _chkFlagStatic = null!;
    private CheckBox _chkHotkey = null!, _chkThrottle = null!, _chkThrottleToast = null!;
    private CheckBox _chkAutoResetDaily = null!, _chkResetOnResume = null!;
    private DateTimePicker _dtpAutoReset = null!;
    private NumericUpDown _numDelay = null!, _numLogInterval = null!, _numLogRetention = null!, _numEcThrottle = null!;
    private Label _lblPawnIo = null!;

    // ================= General tab =================

    private TabPage BuildGeneralTab()
    {
        var page = NewTabPage(Loc.T("set.tab.general"));
        page.AutoScroll = true;

        // AutoSize-label | percent-width-control table so zh-TW label widths and 150% DPI
        // never clip. Hints and the logging group span both columns.
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(12, 12, 12, 12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        int row = 0;
        void AddRow(Control label, Control control, int indent = 0)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            label.Anchor = AnchorStyles.Left;
            control.Anchor = AnchorStyles.Left;
            if (indent > 0)
                label.Margin = new Padding(label.Margin.Left + indent, label.Margin.Top, label.Margin.Right, label.Margin.Bottom);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(control, 1, row);
            row++;
        }
        void AddFullRow(Control control, int indent = 0, bool stretch = false)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Anchor = stretch ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
            if (indent > 0)
                control.Margin = new Padding(control.Margin.Left + indent, control.Margin.Top, control.Margin.Right, control.Margin.Bottom);
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 2);
            row++;
        }
        void AddOptionRow(string tooltipKey, Control label, Control control, int indent = 0)
        {
            SetOptionToolTip(tooltipKey, label, control);
            AddRow(label, control, indent);
        }
        void AddOptionFullRow(string tooltipKey, Control control, int indent = 0, bool stretch = false)
        {
            SetOptionToolTip(tooltipKey, control);
            AddFullRow(control, indent, stretch);
        }

        _cboLanguage = NewCombo(0, 0, 190);
        _cboLanguage.Items.AddRange(new object[] { Loc.T("set.language.auto"), "English", "繁體中文" });
        _cboLanguage.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            Config.Language = _cboLanguage.SelectedIndex switch { 1 => "en", 2 => "zh-TW", _ => "auto" };
        };
        AddOptionRow("tip.general.language", NewLabel(Loc.T("set.language"), 0, 0), _cboLanguage);

        _cboTheme = NewCombo(0, 0, 190);
        _cboTheme.Items.AddRange(new object[] { Loc.T("set.theme.auto"), Loc.T("set.theme.light"), Loc.T("set.theme.dark") });
        _cboTheme.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            Config.ThemeMode = _cboTheme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "auto" };
        };
        AddOptionRow("tip.general.theme", NewLabel(Loc.T("set.theme"), 0, 0), _cboTheme);

        _cboUnits = NewCombo(0, 0, 190);
        _cboUnits.Items.AddRange(new object[] { Loc.T("common.celsius"), Loc.T("common.fahrenheit") });
        _cboUnits.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading) Config.UseFahrenheit = _cboUnits.SelectedIndex == 1;
        };
        AddOptionRow("tip.general.units", NewLabel(Loc.T("set.units"), 0, 0), _cboUnits);

        _cboPoll = NewCombo(0, 0, 190);
        _cboPoll.Items.AddRange(new object[] { Loc.T("main.poll.1s"), Loc.T("main.poll.2s"), Loc.T("main.poll.5s") });
        _cboPoll.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            Config.PollIntervalMs = _cboPoll.SelectedIndex switch { 0 => 1000, 2 => 5000, _ => 2000 };
        };
        AddOptionRow("tip.general.poll_interval", NewLabel(Loc.T("set.poll_interval"), 0, 0), _cboPoll);

        _chkBatteryAdaptive = NewCheck(Loc.T("set.battery_adaptive"), 0, 0);
        _chkBatteryAdaptive.CheckedChanged += (_, _) => { if (!_loading) Config.BatteryAdaptivePolling = _chkBatteryAdaptive.Checked; };
        AddOptionFullRow("tip.general.battery_adaptive", _chkBatteryAdaptive);

        _chkAutostart = NewCheck(Loc.T("set.autostart"), 0, 0);
        _chkAutostart.CheckedChanged += (_, _) =>
        {
            _numDelay.Enabled = _chkAutostart.Checked;
            if (!_loading) Config.StartWithWindows = _chkAutostart.Checked;
        };
        AddOptionFullRow("tip.general.autostart", _chkAutostart);

        _numDelay = NewNumeric(0, 0, 80, 0, 60);
        _numDelay.ValueChanged += (_, _) => { if (!_loading) Config.StartupDelaySeconds = (int)_numDelay.Value; };
        AddOptionRow("tip.general.autostart_delay", NewLabel(Loc.T("set.autostart_delay"), 0, 0), _numDelay, indent: 22);

        var hint = new Label
        {
            Text = Loc.T("set.autostart_hint"),
            AutoSize = true,
            ForeColor = Theme.SubtleText,
        };
        AddFullRow(hint, indent: 22, stretch: true);

        _chkStartMin = NewCheck(Loc.T("set.start_minimized"), 0, 0);
        _chkStartMin.CheckedChanged += (_, _) => { if (!_loading) Config.StartMinimized = _chkStartMin.Checked; };
        AddOptionFullRow("tip.general.start_minimized", _chkStartMin);

        _chkCloseToTray = NewCheck(Loc.T("set.close_to_tray"), 0, 0);
        _chkCloseToTray.CheckedChanged += (_, _) => { if (!_loading) Config.CloseToTray = _chkCloseToTray.Checked; };
        AddOptionFullRow("tip.general.close_to_tray", _chkCloseToTray);

        _chkConfirmClose = NewCheck(Loc.T("set.confirm_on_close"), 0, 0);
        _chkConfirmClose.CheckedChanged += (_, _) => { if (!_loading) Config.ConfirmOnClose = _chkConfirmClose.Checked; };
        AddOptionFullRow("tip.general.confirm_on_close", _chkConfirmClose);

        _chkHotkey = NewCheck(Loc.T("set.hotkey"), 0, 0);
        _chkHotkey.CheckedChanged += (_, _) => { if (!_loading) Config.HotkeyEnabled = _chkHotkey.Checked; };
        AddOptionFullRow("tip.general.hotkey", _chkHotkey);

        _chkShowCores = NewCheck(Loc.T("set.show_cores"), 0, 0);
        _chkShowCores.CheckedChanged += (_, _) => { if (!_loading) Config.ShowPerCoreTemps = _chkShowCores.Checked; };
        AddOptionFullRow("tip.general.show_cores", _chkShowCores);

        _chkFlagStatic = NewCheck(Loc.T("set.flag_static"), 0, 0);
        _chkFlagStatic.CheckedChanged += (_, _) => { if (!_loading) Config.FlagStaticZones = _chkFlagStatic.Checked; };
        AddOptionFullRow("tip.general.flag_static", _chkFlagStatic);

        _chkThrottle = NewCheck(Loc.T("set.throttle"), 0, 0);
        _chkThrottle.CheckedChanged += (_, _) =>
        {
            _chkThrottleToast.Enabled = _chkThrottle.Checked;
            if (!_loading) Config.ThrottleIndicatorEnabled = _chkThrottle.Checked;
        };
        AddOptionFullRow("tip.general.throttle", _chkThrottle);

        _chkThrottleToast = NewCheck(Loc.T("set.throttle_toast"), 0, 0);
        _chkThrottleToast.CheckedChanged += (_, _) => { if (!_loading) Config.ThrottleToast = _chkThrottleToast.Checked; };
        AddOptionFullRow("tip.general.throttle_toast", _chkThrottleToast, indent: 22);

        _chkAutoResetDaily = NewCheck(Loc.T("set.autoreset_daily"), 0, 0);
        _chkAutoResetDaily.CheckedChanged += (_, _) =>
        {
            _dtpAutoReset.Enabled = _chkAutoResetDaily.Checked;
            if (!_loading) Config.AutoResetPeaksDaily = _chkAutoResetDaily.Checked;
        };
        _dtpAutoReset = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm",
            ShowUpDown = true,
            Width = 90,
        };
        _dtpAutoReset.ValueChanged += (_, _) =>
        {
            if (!_loading) Config.AutoResetTime = _dtpAutoReset.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        };
        SetOptionToolTip("tip.general.auto_reset_daily", _chkAutoResetDaily);
        SetOptionToolTip("tip.general.auto_reset_time", _dtpAutoReset);
        AddRow(_chkAutoResetDaily, _dtpAutoReset);

        _chkResetOnResume = NewCheck(Loc.T("set.autoreset_resume"), 0, 0);
        _chkResetOnResume.CheckedChanged += (_, _) => { if (!_loading) Config.ResetPeaksOnResume = _chkResetOnResume.Checked; };
        AddOptionFullRow("tip.general.reset_on_resume", _chkResetOnResume);

        _numEcThrottle = NewNumeric(0, 0, 80, 1, 10);
        _numEcThrottle.ValueChanged += (_, _) => { if (!_loading) Config.EcReadEveryNTicks = (int)_numEcThrottle.Value; };
        AddOptionRow("tip.general.ec_throttle", NewLabel(Loc.T("set.ec_throttle"), 0, 0), _numEcThrottle);

        var grpLog = new GroupBox { Height = 82, ForeColor = Theme.Text };
        _chkLogging = NewCheck(Loc.T("set.logging"), 12, 18);
        _chkLogging.CheckedChanged += (_, _) => { if (!_loading) Config.Logging.Enabled = _chkLogging.Checked; };
        grpLog.Controls.Add(_chkLogging);
        var lblLogInterval = NewLabel(Loc.T("set.logging_interval"), 12, 50);
        grpLog.Controls.Add(lblLogInterval);
        _numLogInterval = NewNumeric(214, 46, 80, 5, 3600);
        _numLogInterval.ValueChanged += (_, _) => { if (!_loading) Config.Logging.IntervalSeconds = (int)_numLogInterval.Value; };
        grpLog.Controls.Add(_numLogInterval);
        var lblLogRetention = NewLabel(Loc.T("set.logging_retention"), 330, 50);
        grpLog.Controls.Add(lblLogRetention);
        _numLogRetention = NewNumeric(540, 46, 80, 1, 365);
        _numLogRetention.ValueChanged += (_, _) => { if (!_loading) Config.Logging.RetentionDays = (int)_numLogRetention.Value; };
        grpLog.Controls.Add(_numLogRetention);
        SetOptionToolTip("tip.general.logging", grpLog, _chkLogging);
        SetOptionToolTip("tip.general.logging_interval", lblLogInterval, _numLogInterval);
        SetOptionToolTip("tip.general.logging_retention", lblLogRetention, _numLogRetention);
        AddFullRow(grpLog, stretch: true);

        _lblPawnIo = NewLabel("", 0, 0);
        _lblPawnIo.ForeColor = Theme.SubtleText;
        SetOptionToolTip("tip.general.pawnio", _lblPawnIo);
        AddFullRow(_lblPawnIo);

        var btnRestore = new Button
        {
            Text = Loc.T("set.restore_defaults"),
            Size = new Size(180, 28),
            Margin = new Padding(3, 10, 3, 3),
        };
        btnRestore.Click += OnRestoreDefaults;
        SetOptionToolTip("tip.general.restore_defaults", btnRestore);
        AddFullRow(btnRestore);

        page.Controls.Add(table);
        return page;
    }

    private void LoadGeneralTab()
    {
        bool prev = _loading;
        _loading = true;
        try
        {
            var c = Config;
            _cboLanguage.SelectedIndex = c.Language == "en" ? 1 : c.Language == "zh-TW" ? 2 : 0;
            _cboTheme.SelectedIndex = c.ThemeMode == "light" ? 1 : c.ThemeMode == "dark" ? 2 : 0;
            _cboUnits.SelectedIndex = c.UseFahrenheit ? 1 : 0;
            _cboPoll.SelectedIndex = c.PollIntervalMs <= 1000 ? 0 : c.PollIntervalMs >= 5000 ? 2 : 1;
            _chkBatteryAdaptive.Checked = c.BatteryAdaptivePolling;
            _chkShowCores.Checked = c.ShowPerCoreTemps;
            _chkFlagStatic.Checked = c.FlagStaticZones;
            _numEcThrottle.Value = Math.Clamp(c.EcReadEveryNTicks, 1, 10);
            _chkAutostart.Checked = c.StartWithWindows;
            _numDelay.Value = Math.Clamp(c.StartupDelaySeconds, 0, 60);
            _numDelay.Enabled = c.StartWithWindows;
            _chkStartMin.Checked = c.StartMinimized;
            _chkCloseToTray.Checked = c.CloseToTray;
            _chkConfirmClose.Checked = c.ConfirmOnClose;
            _chkHotkey.Checked = c.HotkeyEnabled;
            _chkThrottle.Checked = c.ThrottleIndicatorEnabled;
            _chkThrottleToast.Checked = c.ThrottleToast;
            _chkThrottleToast.Enabled = c.ThrottleIndicatorEnabled;
            _chkAutoResetDaily.Checked = c.AutoResetPeaksDaily;
            _dtpAutoReset.Value = ParseAutoResetTime(c.AutoResetTime);
            _dtpAutoReset.Enabled = c.AutoResetPeaksDaily;
            _chkResetOnResume.Checked = c.ResetPeaksOnResume;
            _chkLogging.Checked = c.Logging.Enabled;
            _numLogInterval.Value = Math.Clamp(c.Logging.IntervalSeconds, 5, 3600);
            _numLogRetention.Value = Math.Clamp(c.Logging.RetentionDays, 1, 365);
            _lblPawnIo.Text = Loc.T(_ctx.Sensors.PawnIoDetected ? "set.pawnio.installed" : "set.pawnio.missing");
        }
        finally { _loading = prev; }
    }

    private void OnRestoreDefaults(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("set.restore_defaults.confirm"), Loc.T("set.restore_defaults"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _draftConfig = new AppConfig();
        LoadAllTabs();
    }

    /// <summary>Parses "HH:mm" (invariant) into today's date at that time; midnight on bad input.</summary>
    private static DateTime ParseAutoResetTime(string? text)
        => TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var t)
            ? DateTime.Today.Add(t)
            : DateTime.Today;
}
