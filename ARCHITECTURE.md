# WinMonitor — Architecture Contract

Lightweight hardware monitor for Windows (Core Temp style), .NET 10 WinForms, LibreHardwareMonitorLib 0.9.6/PawnIO backend.
Target machine: LG gram 17 laptop (Intel CPU, NVMe SSD, battery). Must stay lean: no WPF, no chart libraries, no extra NuGet beyond LibreHardwareMonitorLib.

## Hard rules for every module
- Namespace root: `WinMonitor`. Sub-namespaces: `WinMonitor.Core`, `WinMonitor.Config`, `WinMonitor.Tray`, `WinMonitor.UI`, `WinMonitor.Localization`.
- `Nullable` + `ImplicitUsings` enabled. C# 12. Code comments in English.
- All user-visible strings go through `Loc.T("key")` (see Localization/Loc.cs). If you need a new key, use it in code AND report the key with en + zh-TW text in your final summary so it gets merged into Loc.cs.
- No allocations in per-poll hot paths where avoidable: reuse buffers, brushes, fonts, StringBuilder. Never leak GDI handles (every `Icon` created from `CreateIconIndirect`/`GetHicon` must be destroyed with `DestroyIcon`).
- Sensor values may be null/NaN at any time (sensor vanished, driver missing). Never throw on missing data — render "—".
- Events from `SensorService` fire on a background thread. UI/tray consumers must marshal via the `ISynchronizeInvoke` they were constructed with (`Control.BeginInvoke`).
- Temperatures are stored/computed in °C everywhere. Conversion to °F happens only at display time via `Units.Format*` helpers in Models.cs.

## Data flow
```
SensorService (bg thread, LHM Computer)  --SnapshotUpdated event-->
    StatsTracker (session min/max/avg + full export history + bounded chart rings)
    AlertEngine  (threshold + sustain filter -> AlertRaised event)
    TrayIconManager (marshals to UI thread, redraws NotifyIcons)
    MainForm / CompactForm (marshal, update grid/chart)
    HistoryLogger (optional CSV background logging)
```

## Key types (already written — read these files first)
- `Core/Models.cs` — `SensorCategory`, `SensorQuantity`, `SensorDescriptor`, `SensorSnapshot`, `SessionStats`, `Units`, `RingBuffer<T>`.
- **Display names**: `AppConfig.DisplayNameFor` is the single resolver (user rename → storage model prefix → quantity suffix). `WinMonitorContext.RefreshDisplayNames` pushes the result into `SensorDescriptor.DisplayName` at startup, on every descriptor rebuild and on settings apply; the tray, alerts, main list and CSV read that field. LHM reuses one `Name` across a core's temperature/clock/power sensors, so `SensorService.MarkAmbiguousNames` flags names that collide within one hardware and only those gain a `(Temperature)`/`(Frequency)`/… suffix — unique names stay clean. A user rename is used verbatim and never gets a suffix appended.
- `Core/SessionHistoryStore.cs` — append-only temporary-file spool used to preserve full-session CSV history with bounded process memory. Growth is capped (256 MB, then `Truncated`) and `SweepOrphans()` reclaims spools left by runs that did not exit cleanly. Its exporter groups one CSV row per distinct timestamp, which relies on every sensor in a poll tick sharing that tick's timestamp.
- `Core/StartupTimeline.cs` — per-stage cold-start timings, emitted once to the diagnostic log and shown in the Diagnostics tab. Measured on the LG gram: Release/R2R **1225 ms** (+295 ms before `Main`) vs Debug 3789 ms (+1356 ms); `services+ui` — the LHM hardware open and first synchronous poll — is ~94 % of in-`Main` time and is the target for any further startup work.
- `Core/BatteryReport.cs` — design/full-charge capacity and cycle count via the built-in `powercfg /batteryreport /xml`, parsed with `System.Xml.Linq`. LHM reports a degradation percentage but not the capacities behind it. Runs once in the background (never on the startup path or poll thread, the report takes seconds); surfaced as an info row in the Battery group and in Diagnostics.
- `Core/ThermalEventLog.cs` — bounded ring of alerts and throttle transitions, each tagged with the foreground process captured **on the UI thread**. Answers "why did it get hot and what was running", which a fire-and-forget toast cannot. Also breadcrumbed to `Diag` so it survives a crash.
- `Core/Diag.cs` — size-capped rolling breadcrumb log (`winmonitor.log` beside config, one rolled generation). Records lifecycle/degradation events the deliberate empty catches would otherwise hide: backend tier fallbacks, EC reset, rescans, suspend/resume. Never throws.
- `Config/AppConfig.cs` — full JSON config schema: `AppConfig`, `Profile`, `TrayIconConfig`, `Thresholds`, `SensorOverride`, `LoggingConfig`. `ConfigStore` (in ConfigStore.cs) persists it.
- `Localization/Loc.cs` — `Loc.T(key)`, `Loc.Current` ("en" / "zh-TW"), falls back to the key itself, then en.
- `Program.cs` — composition root; shows how everything is wired. `WinMonitorContext` owns all services and the hidden `SyncWindow` used for marshaling + TaskbarCreated re-registration.

## Module contracts (implementors: match these signatures exactly)

### Core/SensorService.cs
```csharp
public sealed class SensorService : IDisposable
{
    public SensorService(AppConfig config);
    public void Start();                       // opens LHM Computer, starts polling timer (background, BelowNormal)
    public void Stop();                        // signals and joins, bounded; refuses to forget a thread it did not see exit
    public IReadOnlyList<SensorDescriptor> Descriptors { get; }   // stable after Start(); refreshed on RescanHardware()
    public event Action<SensorSnapshot[], bool>? SnapshotUpdated; // fired each poll tick, bg thread; bool = every descriptor sampled
    public event Action? DescriptorsChanged;
    public void SetPollInterval(int ms);
    public void SetActiveSensorIds(IReadOnlyCollection<string>? ids); // smart polling: null = update all hardware; otherwise only hardware nodes containing these ids + a full refresh every 30s
    public void RequestFullSweep(bool wakeNow = false); // one-shot full sweep; background logging uses it to keep rows complete without disabling smart polling
    public void RescanHardware();
    public bool IsElevated { get; }            // admin check result
    public bool PawnIoDetected { get; }        // informational, registry/driver check
}
```
- Wrap LibreHardwareMonitorLib `Computer` with `IsCpuEnabled, IsGpuEnabled, IsMemoryEnabled, IsMotherboardEnabled, IsControllerEnabled, IsStorageEnabled, IsBatteryEnabled = true`.
- Sensor Id = `hardware.Identifier + "/" + sensor.Identifier` is redundant; use `sensor.Identifier.ToString()` (already unique + stable).
- Map LHM `SensorType` → `SensorQuantity`; classify `SensorCategory` from `HardwareType` (Fan sensors get category Fan regardless of parent hardware). Only surface quantities we display: Temperature, Fan, Control, Level, Power, Data, Voltage(battery only), Load(CPU total only), and Frequency(CPU clocks in MHz).
- Include WMI fallback `MSAcpi_ThermalZoneTemperature` (root\WMI) as extra "ACPI Thermal Zone" temperature descriptors when elevated; ignore failures silently.
- Poll loop: `System.Threading.Timer`; guard reentrancy; snapshot array reused only if safe — otherwise allocate one array per tick (acceptable) but no LINQ in the tick path.
- **A snapshot is "complete" only when the full node sweep ran *and*, where EC sensors exist, this was one of their throttled read ticks.** Ids missing from a partial snapshot mean "not sampled this tick", never "no reading" — any consumer that records absence as data must wait for a complete snapshot (see `HistoryLogger`). Configured-but-unavailable EC sensors are excluded from the condition; nothing will ever fill them.
- **A native call that never returns cannot be cancelled — only outwaited.** PawnIO's user-mode library issues a synchronous IOCTL whose pending path has no timeout, so no `Stopwatch`, `CancellationToken` or `Task` wrapper can stop it; the only achievable guarantee is that the *caller* stops waiting. `Core/NativeCallGate.cs` runs EC and MSR calls on a dedicated thread and abandons the wait on timeout. The abandoned call still owns the PawnIO handle, the EC mutex and its buffers, so the gate closes permanently (`Wedged`), the feature reports itself unavailable, and **those native objects are deliberately never disposed** — the same rule as `PollThreadHandle`. Do not "fix" a wedged gate by disposing what it left behind.
- **Shutdown may not outrun the poll thread.** A tick wedged in a native `Update()`, an EC read or a rescan still owns the `Computer`, the EC and the wait handles, so `PollThreadHandle` keeps the thread reference until an exit is *observed*: a timed-out `Join` is not an exit. `Dispose` releases natives only on a confirmed exit, otherwise hands them to a background watchdog and, failing that, deliberately leaks them to process exit — an undisposed LHM driver handle is recoverable (`sc.exe delete R0WinMonitor`), a teardown under a live native call is not. `Start` refuses to run a second poll thread while a previous one lives.

### Core/StatsTracker.cs
```csharp
public sealed class StatsTracker : IDisposable
{
    public StatsTracker(int historyCapacity = 3600);
    public void Accept(SensorSnapshot[] snapshots);           // called on bg thread
    public SessionStats? GetStats(string sensorId);
    public float? GetLatestValue(string sensorId);
    public IReadOnlyList<TimedValue> GetHistory(string sensorId); // complete session copy for CSV
    public string ExportTimeSeriesCsv(string path, IReadOnlyList<SensorDescriptor> descriptors); // streaming session export
    public HistoryReadResult GetHistoryIfChanged(string sensorId, long knownVersion); // bounded chart copy
    public void ResetPeaks();                                  // clears stats/chart, keeps export history
    public void Dispose();                                     // closes and deletes the session spool
}
public readonly record struct TimedValue(DateTime Utc, float Value);
```
Track every valid sensor sample so a time-series export contains the complete application run. Keep complete history in an append-only temporary spool and chart history in bounded rings. Thread-safe (lock per call is fine).

Chart rings are **lazily armed**: a ring is allocated only when some consumer first asks for that sensor's history (chart tick, tray sparkline), because at the default capacity each ring costs ~57 KB and most discovered sensors are never plotted. Arming backfills the ring from the disk spool — off the lock, once per sensor — so a chart opened mid-session still shows what already happened rather than starting blank. `GetHistory(id)` is O(entire session) by nature (it filters one sensor out of an interleaved spool); never call it per descriptor in a loop, use `ExportTimeSeriesCsv`, which walks the spool once for all columns.

### Core/AlertEngine.cs
```csharp
public sealed class AlertEngine
{
    public AlertEngine(AppConfig config);
    public void Accept(SensorSnapshot[] snapshots, IReadOnlyList<SensorDescriptor> descriptors); // bg thread
    public event Action<AlertEvent>? AlertRaised;
    public void ReloadConfig();   // call after profile/threshold changes
}
public sealed record AlertEvent(string SensorId, string DisplayName, float Value, float Threshold, bool PlaySound, string? SoundPath);
```
- Threshold resolution order: active profile override → global sensor override → suggested default (`Thresholds.SuggestFor(category, quantity)` in AppConfig.cs).
- Sustain filter: value must stay ≥ red threshold for `SustainSeconds` continuously before raising. After raising, do not re-raise until value drops below yellow threshold OR 10 minutes pass (cooldown).

### Core/HistoryLogger.cs
```csharp
public sealed class HistoryLogger : IDisposable
{
    public HistoryLogger(AppConfig config, Func<IReadOnlyList<SensorDescriptor>> descriptorProvider, string? logDirectory = null);
    public void Accept(SensorSnapshot[] snapshots, bool complete, Action? requestCompleteSnapshot = null); // bg thread; no-op unless config.Logging.Enabled
    public static string ExportTimeSeriesCsv(string path, IReadOnlyList<SensorDescriptor> descriptors, Func<string, IReadOnlyList<TimedValue>> historyProvider);
    public string LogDirectory { get; }
    public void CleanupRetention();  // delete logs older than RetentionDays
}
```
- Background log: one CSV per day `winmonitor-YYYYMMDD.csv`, header = sensor display names, row per logging tick (config.Logging.IntervalSeconds, independent of poll), buffered StreamWriter, flush every 30s.
- **Row completeness is the logger's own responsibility, not a timer's.** When a row falls due on a partial snapshot the logger calls `requestCompleteSnapshot` (wired to `SensorService.RequestFullSweep`) and writes from the complete snapshot that follows — one full sweep per logging interval, no SMART sensor kept active per poll. The request repeats on every due tick, so a lost or mistimed sweep self-corrects. Do not reintroduce a UI timer that requests sweeps on a period *shorter* than the logging interval: it drifts earlier every cycle until the sweep is consumed before the writer is due, and the resulting rows are silently sparse.

### Tray/IconRenderer.cs + Tray/TrayIconManager.cs
```csharp
public static class IconRenderer
{
    public static Icon RenderText(string text, Color fg, Color bg, bool bold);  // 16x16 or 32x32 by SystemInformation.SmallIconSize; caller must Dispose AND DestroyIcon via ReleaseIcon
    public static void ReleaseIcon(Icon icon);
}
public sealed class TrayIconManager : IDisposable
{
    public TrayIconManager(AppConfig config, StatsTracker stats, ISynchronizeInvoke sync);
    public void Rebuild(IReadOnlyList<SensorDescriptor> descriptors);  // create/destroy NotifyIcons per active profile TrayIcons
    public void Accept(SensorSnapshot[] snapshots);                    // bg thread; marshals internally
    public event Action? OpenMainRequested;      // double-click
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;
    public event Action? ResetPeaksRequested;
    public event Action? CompactModeRequested;
    public void ShowToast(string title, string message, ToolTipIcon icon); // balloon on primary icon
    public void RefreshAfterTaskbarRestart();    // re-add icons after TaskbarCreated broadcast
}
```
- One NotifyIcon per `TrayIconConfig`. Multi-sensor configs rotate on a UI-thread WinForms Timer (`RotateIntervalSec`).
- Text is **digits only, never a unit**, and at most three glyphs — a tray icon is 16 px at 100% DPI, and past three glyphs adjacent stems merge into a blob. Magnitudes are folded into the number so no scale is lost: fan RPM in hundreds ("34" = 3400), frequency in GHz ("4.2" = 4200 MHz), data in TB above 1000 GB. Units are stated in the tooltip and main window. `TrayUnitFormattingTests` sweeps every quantity's realistic range against the three-glyph budget. `TrayIconConfig.ShowUnit` is retained for config compatibility but is unused, and its checkbox is gone.
- **Legibility rules in `IconRenderer` (settled by comparing rendered pixels, not by theory — do not "restore" them):** no contrast halo (an 8-way 1 px rim composites to a near-opaque ring that closes glyph counters and turns text to mud on both taskbar shades), and whole-pixel hinting (`SingleBitPerPixelGridFit`) below `AntiAliasMinPx` because grayscale AA smears a 7 px glyph into low contrast. `TrayIconLayoutTests` guards both.
- Color: from threshold state (green/yellow/red from resolved Thresholds) unless ColorOverride. Background transparent by default; `Style` may be TextOnly | TextOnBadge.
- Tooltip (63-char NotifyIcon.Text limit): "Name 45°C (min 38 / max 72)" trimmed to fit. Right-click ContextMenuStrip: Open, Settings, Compact mode, Reset peaks, Open log folder, Task Manager (`taskmgr.exe`), Exit. Only redraw an icon when its rendered text or color actually changed.

### Config/ConfigStore.cs + Config/StartupManager.cs
```csharp
public static class ConfigStore
{
    public static string ConfigDirectory { get; }   // portable mode: exe dir if "portable.txt" next to exe OR config already there; else %AppData%\WinMonitor
    public static bool IsPortable { get; }
    public static AppConfig Load();                 // returns defaults on missing/corrupt (backup corrupt file as .bak)
    public static void Save(AppConfig config);      // atomic: write .tmp then File.Replace
}
public static class StartupManager
{
    public static void Apply(AppConfig config);     // idempotent: registers/unregisters per config.StartWithWindows + StartupDelaySeconds
    public static bool IsRegistered();
    // Installed/elevated: schtasks.exe task "WinMonitor" (onlogon, /RL HIGHEST, delay via XML or fallback);
    // Non-elevated/portable: HKCU\...\Run with `--delay N` argument (Program.cs sleeps before UI).
}
```

### UI (MainForm.cs, SettingsForm.cs, ChartControl.cs, CompactForm.cs)
- MainForm: collapsible groups (CPU / GPU / Storage / Memory / Battery / Fans / Motherboard/Other) — implement with an owner-drawn ListView or a lightweight custom panel list; columns: Name | Current | Min | Max | Avg | extra (RPM shows PWM% in extra; SSD shows health%/TBW; battery shows charge/health/discharge W). Status bar: elevation warning if not admin ("部分感測器需系統管理員權限"), PawnIO hint, active profile combo, poll interval quick-set (1/2/5s), °C/°F toggle button, Reset peaks button, Export CSV button.
- Memory group: if no memory temperature sensor exists show static info line `Loc.T("mem.no_sensor")` ("多數消費級記憶體無獨立溫度感測器").
- ChartControl: pure GDI+ double-buffered control; renders 1–60 min of checked-sensor history from `StatsTracker.GetHistoryIfChanged`; each quantity group has an independent Y scale, series have marker shapes, and line hover shows the sensor name.
- CompactForm: small borderless always-on-top draggable window listing the active profile's tray sensors (name + value, threshold colors); right-click menu to return to full window; remembers position in config.
- SettingsForm sizes itself to its busiest tab in `OnLoad` (`FitTabsToContent`) instead of using a fixed size: absolute layouts sized for English at 100% DPI overflow with longer zh-TW labels, and the overflowing controls were unreachable. Every tab page also sets `AutoScroll` as the guarantee when the computed size has to be clamped to the working area.
- SettingsForm tabs: General (language, units, poll interval, autostart+delay, close-to-tray+confirm, logging on/off+interval+retention) | Tray icons (list of TrayIconConfig entries: add/remove/reorder, per-entry sensor multi-select+rotate interval+style+show unit+color override) | Sensors (grid of all descriptors: rename, hide, per-sensor yellow/red thresholds with "suggest" button) | Alerts (enable per sensor via same grid; sustain seconds; sound picker + test) | Profiles (add/clone/delete/switch; "Restore defaults" button wipes to `new AppConfig()` after confirm).
- MainForm close button → hide to tray when config.CloseToTray (with optional confirm dialog + "don't ask again"); real exit only via tray menu or File→Exit.

## Embedded Controller subsystem (LG fan support)
- `Core/PawnIo.cs` — P/Invoke over PawnIOLib.dll (resolved from `%ProgramFiles%\PawnIO`), stdcall/HRESULT ABI: open/load/execute/close.
- `Core/EmbeddedController.cs` — read-only ACPI EC access via the signed `pawnio/LpcACPIEC.bin` module (ports 0x62/0x66). NEVER writes EC registers. Three rules hold this together and each exists because the alternative is a wrong reading or a stall:
  - **`Global\Access_EC` or nothing.** Create-or-open that exact name — a session-local `Access_EC` is a different kernel object and would synchronize against nobody while ACPI.sys drives the same ports. If it cannot be obtained the EC stays unavailable (`ec_mutex_unavailable`); there is deliberately no fallback.
  - **One budget covers every wait.** The stopwatch starts before the managed lock, and the lock, the shared mutex, the OBF drain and each register handshake all draw from it (poll thread 40 ms, Explorer dump 250 ms off the UI thread). A fixed mutex wait that outlives the budget is the bug this replaced.
  - **Batches run on a `NativeCallGate`.** The budget bounds our waiting, not the kernel's; a wedged IOCTL costs the caller its timeout and then disables the EC for the session (`ec_wedged`), leaving the driver handle and the mutex to the abandoned call.
- `Config/EcConfig.cs` — `EcConfig { Enabled, List<EcSensorDef> }`; `EcSensorDef` maps a register (or register pair) → sensor value (RawByte/Word/Percent/RpmDirect/RpmDivided). Sensor id `/ec/reg/XX/Kind`.
- `Config/KnownEcProfiles.cs` — exact-machine, one-time defaults only. The LG gram 360 `16T90R` / `gram360` / `GP*` profile maps DSDT fields `RPM1/RPM2` at `0xB0/0xB1` as LE16 direct RPM. `AppliedDefaultProfile` prevents a user-deleted suggestion from being re-added.
- `UI/EcExplorerForm.cs` — legacy diagnostic UI retained in source; intentionally not exposed in MainForm now that the production `16T90R` fan mapping is known.
- SensorService integration: `Ec` accessor, `RefreshEcSensors(EcConfig)` (deep-copies to an immutable snapshot the poll thread reads — never touches the live list), `BuildEcDescriptors` reads only the exact registers needed via `ReadRegisters`. `AppConfig.Ec`.

## Program.cs wiring (already written — do not change signatures it relies on)
Single instance via named mutex `Global\WinMonitor_SingleInstance` + named pipe activation (second launch → first instance shows MainForm). Args: `--minimized` (sensor-only start), `--delay <sec>`.
