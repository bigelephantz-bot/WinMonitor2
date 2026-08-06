using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;
using WinMonitor.Tray;
using WinMonitor.UI;

namespace WinMonitor;

internal static class Program
{
    private const string MutexName = "Global\\WinMonitor_SingleInstance";
    private const string PipeName = "WinMonitor.Activate";

    [STAThread]
    private static void Main(string[] args)
    {
        bool minimized = false;
        int delaySeconds = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--minimized", StringComparison.OrdinalIgnoreCase)) minimized = true;
            else if (args[i].Equals("--delay", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                     && int.TryParse(args[i + 1], out var d)) delaySeconds = Math.Clamp(d, 0, 300);
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            SignalExistingInstance();
            return;
        }

        // Startup delay (Run-key autostart path; Task Scheduler handles its own delay natively).
        if (delaySeconds > 0)
            Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));

        AppConfig config = ConfigStore.Load();
        Loc.Initialize(config.Language);
        Theme.Initialize(config.ThemeMode);
        Units.UseFahrenheit = config.UseFahrenheit;
        if (config.StartMinimized) minimized = true;

        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using var context = new WinMonitorContext(config, minimized);
        Application.Run(context);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.WriteLine("SHOW");
            writer.Flush();
        }
        catch
        {
            // First instance is starting up or hung; nothing sensible to do.
        }
    }

    internal static void LogCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            string path = Path.Combine(ConfigStore.ConfigDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { /* never throw from the crash logger */ }
    }

    internal static string ActivationPipeName => PipeName;
}

/// <summary>
/// Composition root. Owns every service and window; the app lives in the tray, so this —
/// not a Form — is the Application.Run context. All services communicate through here.
/// </summary>
public sealed class WinMonitorContext : ApplicationContext
{
    private AppConfig _config;
    /// <summary>Current immutable-by-convention configuration reference, atomically replaced on Apply.</summary>
    public AppConfig Config => Volatile.Read(ref _config);
    public SensorService Sensors { get; }
    public StatsTracker Stats { get; }
    public AlertEngine Alerts { get; }
    public HistoryLogger Logger { get; }
    public TrayIconManager Tray { get; }

    /// <summary>Raised on the UI thread after ApplySettings finishes propagating a change.</summary>
    public event Action? SettingsApplied;

    private readonly SyncWindow _sync;
    private MainForm? _mainForm;
    private CompactForm? _compactForm;
    private SettingsForm? _settingsForm;
    private FlyoutForm? _flyout;
    private Thread? _pipeThread;
    private volatile bool _exiting;
    private bool _sessionEndingHooked;
    private bool _powerModeHooked;

    // Auto peak reset (item 15): 1-minute schedule check + a guard so one target minute
    // never triggers twice. -1 = never auto-reset this session.
    private readonly System.Windows.Forms.Timer _peakResetTimer;
    private long _lastAutoPeakResetTick = -1;

    // Throttle-indicator enabled state at the last ApplySettings. When it flips, the descriptor
    // list must be rebuilt (the throttle pseudo-sensor is added/removed there) to take effect now.
    private bool _lastThrottleEnabled;

    // Startup registration may invoke schtasks.exe and take several seconds. Serialize it off the
    // UI thread, and discard superseded requests before they touch the registry/task scheduler.
    private readonly SemaphoreSlim _startupRegistrationGate = new(1, 1);
    private int _startupRegistrationGeneration;

    // Latest CPU package temperature / total load, published for the EC finder (see LatestCpuThermal).
    // Written on the snapshot (background) thread; read on the UI thread by the EC Explorer.
    private volatile float _lastCpuTemp = float.NaN;
    private volatile float _lastCpuLoad = float.NaN;
    // Cached descriptor ids so OnSnapshot doesn't rescan the descriptor list each tick; recomputed
    // only when the Descriptors reference changes (rescan / EC edit).
    private object? _cpuThermalDescriptorsRef;
    private string? _cpuTempId;
    private string? _cpuLoadId;

    public WinMonitorContext(AppConfig config, bool startMinimized)
    {
        _config = config;
        _lastThrottleEnabled = config.ThrottleIndicatorEnabled;

        _sync = new SyncWindow();
        _sync.TaskbarRestarted += () => Tray?.RefreshAfterTaskbarRestart();
        _sync.HotkeyPressed += OnHotkeyPressed;

        Sensors = new SensorService(() => Config);
        Stats = new StatsTracker();
        Alerts = new AlertEngine(() => Config);
        Logger = new HistoryLogger(() => Config, () => Sensors.Descriptors);
        Tray = new TrayIconManager(() => Config, Stats, _sync);

        Tray.OpenMainRequested += ShowMainWindow;
        Tray.OpenSettingsRequested += ShowSettings;
        Tray.ExitRequested += ExitApp;
        Tray.ResetPeaksRequested += ResetPeaks;
        Tray.CompactModeRequested += ShowCompact;
        Tray.FlyoutRequested += OnFlyoutRequested;

        Alerts.AlertRaised += OnAlert;

        Sensors.SnapshotUpdated += OnSnapshot;
        Sensors.DescriptorsChanged += OnDescriptorsChanged;
        Sensors.ThrottleStateChanged += OnThrottleStateChanged;
        Sensors.Start();
        PruneSuppressedStorageTemperatureLimitReferences();
        RefreshDisplayNames();
        Tray.Rebuild(Sensors.Descriptors);
        UpdateActiveSensorSet();

        Logger.CleanupRetention();

        // Save config on Windows shutdown/logoff; ExitApp otherwise never runs in that path.
        if (!_sessionEndingHooked)
        {
            Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
            _sessionEndingHooked = true;
        }

        // React to AC/battery transitions (adaptive polling) and to resume-from-sleep (stale
        // hardware/EC handle recovery). Guard against double-subscribe like SessionEnding.
        if (!_powerModeHooked)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _powerModeHooked = true;
        }

        ApplyEffectivePollInterval();

        // Global hotkey (toggle compact overlay). Failure (combo taken) is logged, never fatal.
        ApplyHotkeyRegistration();

        // Auto peak reset: cheap once-a-minute schedule check; only ticks while the loop runs.
        _peakResetTimer = new System.Windows.Forms.Timer { Interval = 60000 };
        _peakResetTimer.Tick += (_, _) => AutoResetPeaksTick();
        _peakResetTimer.Start();

        // Repair autostart registration if the exe moved since last run without delaying startup.
        QueueStartupRegistration(reportFailure: false);

        StartActivationPipe();

        if (!startMinimized)
        {
            if (config.CompactMode) ShowCompact();
            else ShowMainWindow();
        }
    }

    // ---------- data fan-out ----------

    private void OnSnapshot(SensorSnapshot[] snapshots)
    {
        // Background thread. Order matters: stats first so consumers see fresh min/max.
        Stats.Accept(snapshots);
        Alerts.Accept(snapshots, Sensors.Descriptors);
        Tray.Accept(snapshots);
        Logger.Accept(snapshots);
        UpdateCpuThermal(snapshots);

        var main = _mainForm;
        if (main is { IsDisposed: false, Visible: true }) main.AcceptSnapshots(snapshots);
        var compact = _compactForm;
        if (compact is { IsDisposed: false, Visible: true }) compact.AcceptSnapshots(snapshots);
        var fly = _flyout;
        if (fly is { IsDisposed: false, Visible: true }) fly.AcceptSnapshots(snapshots);
    }

    /// <summary>
    /// Publishes the latest CPU package temp and CPU total load for the EC finder. Runs on the
    /// background snapshot thread. Descriptor ids are cached and only recomputed when the
    /// Descriptors reference changes, so the per-tick cost is a couple of linear scans.
    /// </summary>
    private void UpdateCpuThermal(SensorSnapshot[] snapshots)
    {
        var descriptors = Sensors.Descriptors;
        if (!ReferenceEquals(descriptors, _cpuThermalDescriptorsRef))
        {
            _cpuThermalDescriptorsRef = descriptors;
            _cpuTempId = SensorPicker.PickAuto(descriptors);
            _cpuLoadId = null;
            for (int i = 0; i < descriptors.Count; i++)
            {
                var d = descriptors[i];
                if (d.Category == SensorCategory.Cpu && d.Quantity == SensorQuantity.Load)
                {
                    _cpuLoadId = d.Id;
                    break;
                }
            }
        }

        _lastCpuTemp = ReadSnapshotValue(snapshots, _cpuTempId);
        _lastCpuLoad = ReadSnapshotValue(snapshots, _cpuLoadId);
    }

    private static float ReadSnapshotValue(SensorSnapshot[] snapshots, string? id)
    {
        if (id is null) return float.NaN;
        for (int i = 0; i < snapshots.Length; i++)
        {
            if (snapshots[i].Id == id)
            {
                float? v = snapshots[i].Value;
                return v.HasValue ? v.Value : float.NaN;
            }
        }
        return float.NaN;
    }

    /// <summary>Latest CPU package temperature / total load (NaN when unavailable). Thread-safe.</summary>
    public (float temp, float load) LatestCpuThermal() => (_lastCpuTemp, _lastCpuLoad);

    private void OnDescriptorsChanged()
    {
        // Raised on the polling thread; everything below reads config, so marshal it all.
        InvokeOnUi(() =>
        {
            PruneSuppressedStorageTemperatureLimitReferences();
            RefreshDisplayNames();
            Tray.Rebuild(Sensors.Descriptors);
            _mainForm?.ReloadSensors();
            _compactForm?.ReloadSensors();
            if (_flyout is { IsDisposed: false }) _flyout.ReloadSensors();
            UpdateActiveSensorSet();
        });
    }

    private void RefreshDisplayNames()
    {
        foreach (var d in Sensors.Descriptors)
            d.DisplayName = Config.DisplayNameFor(d);
    }

    /// <summary>
    /// Removes references to fixed NVMe SMART warning/critical limits only after SensorService
    /// positively classified and suppressed them during a descriptor rebuild. Clone first, then
    /// publish one replacement so the poll thread never sees an in-place collection edit.
    /// </summary>
    private void PruneSuppressedStorageTemperatureLimitReferences()
    {
        IReadOnlyList<string> suppressedIds = Sensors.SuppressedStorageTemperatureLimitSensorIds;
        if (suppressedIds.Count == 0) return;

        AppConfig candidate = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
        if (!ConfigStore.PruneSuppressedStorageTemperatureLimitReferences(candidate, suppressedIds)) return;
        ReplaceConfig(candidate);
        ConfigStore.Save(candidate);
    }

    private void OnAlert(AlertEvent e)
    {
        InvokeOnUi(() =>
        {
            var d = FindDescriptor(e.SensorId);
            string value = d is null ? e.Value.ToString("0.#") : Units.Format(d.Quantity, e.Value);
            string threshold = d is null ? e.Threshold.ToString("0.#") : Units.Format(d.Quantity, e.Threshold);
            Tray.ShowToast(Loc.T("alert.title"), Loc.F("alert.body", e.DisplayName, value, threshold), ToolTipIcon.Warning);
            if (e.PlaySound) PlayAlertSound(e.SoundPath);
        });
    }

    private void OnThrottleStateChanged(bool throttling)
    {
        // Poll thread. Toast only on entry into the throttled state (debounce lives in SensorService).
        if (!throttling || !Config.ThrottleToast) return;
        InvokeOnUi(() => Tray.ShowToast(Loc.T("throttle.toast_title"), Loc.T("throttle.toast_body"), ToolTipIcon.Warning));
    }

    private static void PlayAlertSound(string? soundPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(soundPath) && File.Exists(soundPath))
                new System.Media.SoundPlayer(soundPath).Play();
            else
                System.Media.SystemSounds.Exclamation.Play();
        }
        catch { /* audio device issues must never break monitoring */ }
    }

    private SensorDescriptor? FindDescriptor(string id)
    {
        var list = Sensors.Descriptors;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == id) return list[i];
        return null;
    }

    /// <summary>
    /// Smart polling: when no window is visible, only poll hardware backing tray icons and
    /// alert-enabled sensors. Any visible window switches to full polling.
    /// </summary>
    public void UpdateActiveSensorSet()
    {
        bool windowVisible = (_mainForm is { IsDisposed: false, Visible: true } && _mainForm.WindowState != FormWindowState.Minimized)
                          || (_compactForm is { IsDisposed: false, Visible: true })
                          || (_settingsForm is { IsDisposed: false, Visible: true });
        // Background CSV promises a complete row at its configured interval. Smart polling must
        // therefore keep every descriptor active while logging, even with all windows hidden.
        if (windowVisible || Config.Logging.Enabled)
        {
            Sensors.SetActiveSensorIds(null);
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var icon in Config.Active.TrayIcons)
        {
            if (icon.SensorIds.Count == 0)
            {
                // Icon in auto mode: poll the sensor SensorPicker will render.
                var auto = SensorPicker.PickAuto(Sensors.Descriptors);
                if (auto is not null) ids.Add(auto);
                continue;
            }
            foreach (var id in icon.SensorIds)
                ids.Add(id);
        }
        foreach (var d in Sensors.Descriptors)
            if (Config.ResolveThresholds(d).AlertEnabled) ids.Add(d.Id);
        Sensors.SetActiveSensorIds(ids);
    }

    // ---------- windows ----------

    public void ShowMainWindow()
    {
        InvokeOnUi(() =>
        {
            Config.CompactMode = false;
            if (_compactForm is { IsDisposed: false }) _compactForm.Hide();
            if (_mainForm is null || _mainForm.IsDisposed)
            {
                _mainForm = new MainForm(this);
                _mainForm.VisibleChanged += (_, _) => UpdateActiveSensorSet();
            }
            _mainForm.Show();
            if (_mainForm.WindowState == FormWindowState.Minimized)
                _mainForm.WindowState = FormWindowState.Normal;
            _mainForm.Activate();
            UpdateActiveSensorSet();
        });
    }

    private EcExplorerForm? _ecExplorer;

    /// <summary>Opens the Embedded Controller explorer (LG fan discovery). Single instance.</summary>
    public void ShowEcExplorer(IWin32Window? owner = null)
    {
        InvokeOnUi(() =>
        {
            if (_ecExplorer is { IsDisposed: false })
            {
                _ecExplorer.Activate();
                return;
            }
            try { Sensors.Ec.Initialize(); } catch { }
            _ecExplorer = new EcExplorerForm(Sensors.Ec, Config.Ec, OnEcSensorsChanged, LatestCpuThermal);
            _ecExplorer.FormClosed += (_, _) => _ecExplorer = null;
            _ecExplorer.Show();
        });
    }

    private void OnEcSensorsChanged()
    {
        // Runs on the UI thread, which owns Config.Ec.Sensors. RefreshEcSensors deep-copies the
        // list here, so the poll thread only ever sees an immutable snapshot.
        Config.Ec.Enabled = Config.Ec.Enabled || Config.Ec.Sensors.Count > 0;
        ConfigStore.Save(Config);
        Sensors.RefreshEcSensors(Config.Ec);
    }

    public void ShowCompact()
    {
        InvokeOnUi(() =>
        {
            Config.CompactMode = true;
            if (_mainForm is { IsDisposed: false }) _mainForm.Hide();
            if (_compactForm is null || _compactForm.IsDisposed)
            {
                _compactForm = new CompactForm(this);
                _compactForm.VisibleChanged += (_, _) => UpdateActiveSensorSet();
            }
            _compactForm.Show();
            _compactForm.Activate();
            UpdateActiveSensorSet();
        });
    }

    // ---------- global hotkey (toggle compact overlay) ----------

    /// <summary>Applies the hotkey registration to match current config (unregister + register).</summary>
    private void ApplyHotkeyRegistration()
    {
        _sync.UnregisterCompactHotkey();
        if (!Config.HotkeyEnabled) return;
        if (!_sync.RegisterCompactHotkey(Config.HotkeyModifiers, Config.HotkeyKey))
            Program.LogCrash(new InvalidOperationException(
                $"RegisterHotKey failed (modifiers=0x{Config.HotkeyModifiers:X}, key=0x{Config.HotkeyKey:X}); the combination is likely taken by another app."));
    }

    private void OnHotkeyPressed()
    {
        // Raised from SyncWindow.WndProc, so we are already on the UI thread.
        if (_compactForm is { IsDisposed: false, Visible: true }) _compactForm.Hide();
        else ShowCompact();
    }

    // ---------- tray flyout ----------

    private void OnFlyoutRequested(IReadOnlyList<string> sensorIds, Point screenPos)
    {
        InvokeOnUi(() =>
        {
            if (_flyout is null || _flyout.IsDisposed) _flyout = new FlyoutForm(this);
            _flyout.ReloadSensors();
            _flyout.ShowFor(sensorIds, screenPos);
        });
    }

    public void ShowSettings()
    {
        InvokeOnUi(() =>
        {
            if (_settingsForm is { IsDisposed: false })
            {
                _settingsForm.Activate();
                return;
            }
            _settingsForm = new SettingsForm(this);
            _settingsForm.FormClosed += (_, _) => { _settingsForm = null; UpdateActiveSensorSet(); };
            _settingsForm.Show();
        });
    }

    public void ResetPeaks()
    {
        Stats.ResetPeaks();
        InvokeOnUi(() => _mainForm?.ReloadSensors());
    }

    /// <summary>
    /// Persists and rebuilds only the active profile's tray icons. Row-level tray toggles do
    /// not alter any sensor descriptor, display setting, or threshold, so routing them through
    /// <see cref="ApplySettings"/> would unnecessarily rebuild the main list and lose its
    /// scroll position.
    /// </summary>
    public void ApplyTrayIconConfiguration()
    {
        ConfigStore.Save(Config);
        InvokeOnUi(() =>
        {
            Tray.Rebuild(Sensors.Descriptors);
            _compactForm?.ReloadSensors();
            if (_flyout is { IsDisposed: false }) _flyout.ReloadSensors();
            // Do not raise SettingsApplied: a modeless SettingsForm uses that event to advance
            // its Cancel snapshot, while this row-level tray action must remain isolated.
        });
        UpdateActiveSensorSet();
    }

    /// <summary>
    /// Publishes a complete deserialized configuration in one reference write. Services receive
    /// the replacement through their config providers, so a background poll never observes a
    /// half-copied profile, alert list, or logging block.
    /// </summary>
    internal void ReplaceConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Interlocked.Exchange(ref _config, config);
    }

    /// <summary>
    /// 60-second timer tick (UI thread): fires ResetPeaks when the local time reaches the
    /// configured daily HH:mm. The 2-minute guard keeps a single target minute from resetting
    /// twice when ticks land close together.
    /// </summary>
    private void AutoResetPeaksTick()
    {
        if (!Config.AutoResetPeaksDaily) return;
        long now = Environment.TickCount64;
        if (_lastAutoPeakResetTick >= 0 && now - _lastAutoPeakResetTick < 120_000) return;
        if (!TimeSpan.TryParse(Config.AutoResetTime, CultureInfo.InvariantCulture, out var at)) return;
        var local = DateTime.Now;
        if (local.Hour != at.Hours || local.Minute != at.Minutes) return;
        _lastAutoPeakResetTick = now;
        ResetPeaks();
    }

    /// <summary>
    /// Starts a serialized, background autostart registration using only the two settings the
    /// registration code consumes. A later Settings Apply supersedes queued work before it runs.
    /// </summary>
    private void QueueStartupRegistration(bool reportFailure)
    {
        var snapshot = new AppConfig
        {
            StartWithWindows = Config.StartWithWindows,
            StartupDelaySeconds = Config.StartupDelaySeconds,
        };
        int generation = Interlocked.Increment(ref _startupRegistrationGeneration);
        ThreadPool.QueueUserWorkItem(state =>
        {
            _ = ApplyStartupRegistrationAsync(snapshot, generation, reportFailure);
        });
    }

    private async Task ApplyStartupRegistrationAsync(AppConfig snapshot, int generation, bool reportFailure)
    {
        Exception? failure = null;
        await _startupRegistrationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_exiting || generation != Volatile.Read(ref _startupRegistrationGeneration)) return;
            StartupManager.Apply(snapshot);
        }
        catch (Exception ex)
        {
            failure = ex;
            Program.LogCrash(ex);
        }
        finally
        {
            _startupRegistrationGate.Release();
        }

        if (failure is null || !reportFailure || !snapshot.StartWithWindows) return;
        InvokeOnUi(() =>
        {
            // Do not overwrite a newer user choice with a delayed failure from an old request.
            if (_exiting
                || generation != Volatile.Read(ref _startupRegistrationGeneration)
                || !Config.StartWithWindows
                || Config.StartupDelaySeconds != snapshot.StartupDelaySeconds)
            {
                return;
            }

            Config.StartWithWindows = false;
            try { ConfigStore.Save(Config); } catch { }
            MessageBox.Show(
                Loc.F("startup.register_failed", failure.Message),
                Loc.T("common.error"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SettingsApplied?.Invoke();
        });
    }

    /// <summary>Persists and applies edits already made directly to the live configuration.</summary>
    public void ApplySettings()
    {
        ConfigStore.Save(Config);
        ApplySettingsCore();
    }

    /// <summary>
    /// Persists a complete settings candidate before publishing it. A save failure therefore
    /// leaves both the live reference and the running services on their previous configuration.
    /// </summary>
    internal void ApplySettings(AppConfig candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ConfigStore.Save(candidate);
        ReplaceConfig(candidate);
        ApplySettingsCore();
    }

    private void ApplySettingsCore()
    {
        Loc.Initialize(Config.Language);
        bool wasDark = Theme.IsDark;
        Theme.Initialize(Config.ThemeMode);
        bool themeChanged = Theme.IsDark != wasDark;
        Units.UseFahrenheit = Config.UseFahrenheit;
        ApplyEffectivePollInterval();
        Alerts.ReloadConfig();
        RefreshDisplayNames();
        // Enabling/disabling the throttle indicator adds/removes its pseudo-sensor, so the
        // descriptor list must be rebuilt for the change to take effect immediately.
        if (Config.ThrottleIndicatorEnabled != _lastThrottleEnabled)
        {
            _lastThrottleEnabled = Config.ThrottleIndicatorEnabled;
            Sensors.RequestDescriptorRebuild();
        }
        QueueStartupRegistration(reportFailure: true);
        InvokeOnUi(() =>
        {
            Tray.Rebuild(Sensors.Descriptors);
            if (themeChanged)
            {
                RecreateThemedWindows();
            }
            else
            {
                _mainForm?.ReloadSensors();
                _compactForm?.ReloadSensors();
            }
            ApplyHotkeyRegistration();
        });
        UpdateActiveSensorSet();
        InvokeOnUi(() => SettingsApplied?.Invoke());
    }

    /// <summary>
    /// Theme colors are baked into controls at construction, so when the effective light/dark
    /// state flips, the cached main/compact/flyout windows are disposed and — if one was
    /// visible — recreated on the spot. Runs on the UI thread. The settings window (the caller)
    /// is left alone; it picks up the theme the next time it opens.
    /// </summary>
    private void RecreateThemedWindows()
    {
        bool mainVisible = _mainForm is { IsDisposed: false, Visible: true };
        bool compactVisible = _compactForm is { IsDisposed: false, Visible: true };
        _mainForm?.Dispose();
        _mainForm = null;
        _compactForm?.Dispose();
        _compactForm = null;
        _flyout?.Dispose();
        _flyout = null;
        if (mainVisible) ShowMainWindow();
        else if (compactVisible) ShowCompact();
    }

    private void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e) => ExitApp();

    private void UnhookSessionEnding()
    {
        // SystemEvents holds static references; leaking the handler keeps this context alive.
        if (!_sessionEndingHooked) return;
        _sessionEndingHooked = false;
        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
    }

    /// <summary>
    /// Applies the poll interval, doubling (× BatteryPollMultiplier) it while running on battery
    /// if adaptive polling is enabled. Called from the ctor, ApplySettings and on power changes.
    /// </summary>
    private void ApplyEffectivePollInterval()
    {
        int eff = Config.PollIntervalMs;
        if (Config.BatteryAdaptivePolling
            && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
        {
            eff *= Math.Max(1, Config.BatteryPollMultiplier);
        }
        Sensors.SetPollInterval(eff);
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case Microsoft.Win32.PowerModes.StatusChange:
                // AC <-> battery transition: re-evaluate the effective interval.
                InvokeOnUi(ApplyEffectivePollInterval);
                break;
            case Microsoft.Win32.PowerModes.Resume:
                // Resume from sleep: interval may need re-evaluating and hardware/EC handles can be
                // stale, so rescan to recover them (also hardens against post-sleep flakiness).
                InvokeOnUi(() =>
                {
                    ApplyEffectivePollInterval();
                    Sensors.RescanHardware();
                    if (Config.ResetPeaksOnResume) ResetPeaks();
                });
                break;
        }
    }

    private void UnhookPowerMode()
    {
        if (!_powerModeHooked) return;
        _powerModeHooked = false;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    public void ExitApp()
    {
        if (_exiting) return;
        // If a settings dialog is still open, close it first so its OnFormClosing revert runs;
        // otherwise the ConfigStore.Save below would persist edits the user never committed.
        if (_settingsForm is { IsDisposed: false } sf) { try { sf.Close(); } catch { } }
        _exiting = true;
        UnhookSessionEnding();
        UnhookPowerMode();
        CleanupOwnedResources();
        ExitThread();
    }

    private void InvokeOnUi(Action action)
    {
        if (_exiting) return;
        try
        {
            if (_sync.InvokeRequired) _sync.BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // ---------- single-instance activation ----------

    private void StartActivationPipe()
    {
        _pipeThread = new Thread(() =>
        {
            while (!_exiting)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        Program.ActivationPipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    if (reader.ReadLine() == "SHOW" && !_exiting)
                        ShowMainWindow();
                }
                catch
                {
                    if (_exiting) return;
                    Thread.Sleep(500);
                }
            }
        })
        { IsBackground = true, Name = "WinMonitor.ActivationPipe", Priority = ThreadPriority.Lowest };
        _pipeThread.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting)
        {
            _exiting = true;
            UnhookSessionEnding();
            UnhookPowerMode();
            CleanupOwnedResources();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Best-effort, ordered teardown. Every step is isolated so one failing save or native
    /// cleanup cannot skip the remaining handles, files, subscriptions, or polling thread.
    /// </summary>
    private void CleanupOwnedResources()
    {
        TryCleanup(() => ConfigStore.Save(Config));
        TryCleanup(() => Sensors.SnapshotUpdated -= OnSnapshot);
        TryCleanup(() => Sensors.DescriptorsChanged -= OnDescriptorsChanged);
        TryCleanup(() => Sensors.ThrottleStateChanged -= OnThrottleStateChanged);
        TryCleanup(() => Alerts.AlertRaised -= OnAlert);
        TryCleanup(Sensors.Stop);
        TryCleanup(_peakResetTimer.Stop);
        TryCleanup(_peakResetTimer.Dispose);
        TryCleanup(_sync.UnregisterCompactHotkey);
        TryCleanup(() => _flyout?.Dispose());
        TryCleanup(Tray.Dispose);
        TryCleanup(Logger.Dispose);
        TryCleanup(Stats.Dispose);
        TryCleanup(Sensors.Dispose);
        TryCleanup(_sync.Dispose);
    }

    private static void TryCleanup(Action action)
    {
        try { action(); }
        catch (Exception ex) { Program.LogCrash(ex); }
    }
}

/// <summary>
/// Hidden window used as the UI-thread marshaling anchor (ISynchronizeInvoke) and to catch
/// the "TaskbarCreated" broadcast so tray icons survive an Explorer restart.
/// </summary>
public sealed class SyncWindow : Form
{
    private static readonly int WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x574D; // 'WM'

    public event Action? TaskbarRestarted;
    /// <summary>Raised on the UI thread when the registered global hotkey is pressed.</summary>
    public event Action? HotkeyPressed;

    private bool _hotkeyRegistered;

    public SyncWindow()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        Opacity = 0;
        // Force native handle creation so BeginInvoke works before/without ever showing.
        _ = Handle;
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    /// <summary>Registers the global compact-overlay hotkey. False when the combo is taken.</summary>
    public bool RegisterCompactHotkey(int modifiers, int key)
    {
        UnregisterCompactHotkey();
        _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, modifiers, key);
        return _hotkeyRegistered;
    }

    public void UnregisterCompactHotkey()
    {
        if (!_hotkeyRegistered) return;
        _hotkeyRegistered = false;
        // If the handle is already gone the hotkey died with it; don't force a new handle.
        if (IsHandleCreated) UnregisterHotKey(Handle, HotkeyId);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmTaskbarCreated)
            TaskbarRestarted?.Invoke();
        else if (m.Msg == WmHotkey && m.WParam == HotkeyId)
            HotkeyPressed?.Invoke();
        base.WndProc(ref m);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
