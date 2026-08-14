# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

WinMonitor — a lightweight Core-Temp-style tray hardware monitor for Windows (.NET 10 WinForms + LibreHardwareMonitorLib 0.9.6 + PawnIO for MSR/EC access). Bilingual (en + zh-TW); the primary user speaks Traditional Chinese, so user-facing replies should be zh-TW while code and comments stay English.

`AGENTS.md` holds the repo's contributor conventions and `ARCHITECTURE.md` is the binding module contract (public signatures, data flow, threading rules) — read `ARCHITECTURE.md` before touching Core, Tray, EC access, or UI threading. `docs/` carries hardware notes; `dist/` is generated publish output and is never hand-edited.

## Build / test / run

**The system-wide `dotnet` is runtime-only — it has no SDK.** `AGENTS.md` lists bare `dotnet` commands; on this machine they fail. Use the user-local SDK (10.0.301):

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build .\src\WinMonitor\WinMonitor.csproj -c Debug --nologo -v minimal
```

Regression harness — a package-free console Exe (no xUnit), not a `dotnet test` project:

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project .\tests\WinMonitor.Tests\WinMonitor.Tests.csproj -c Release
```

It runs a hardcoded array of checks in `CoreRegressionTests.cs`, prints `PASS`/`FAIL` per name, and exits non-zero on failure. **There is no per-test filter argument** — to run one check in isolation, temporarily trim the `tests` array at the top of the file. Add new checks as a `static void <Area>Tests()` plus an entry in that array.

Publish both flavors (installed + portable, framework-dependent win-x64, staged then swapped into `dist`):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

`publish.ps1` runs the regression harness first and aborts on failure; `-SkipTests` bypasses that deliberately. It also resolves an SDK itself (system → user-local → a Codex workspace SDK under `%TEMP%\dotnet-sdk-*`).

Sensor diagnostic (console, project-references the app): `dotnet run --project .\tools\SensorDump\SensorDump.csproj`.

Smoke test without a UAC prompt — the app manifest is `highestAvailable` and the dev shell is not elevated:

```powershell
$env:__COMPAT_LAYER = "RunAsInvoker"
Start-Process .\src\WinMonitor\bin\Debug\net10.0-windows\WinMonitor.exe
```

Non-elevated runs are degraded **by design** (battery + CPU load only; CPU temps, NVMe SMART, MSR and EC all need admin). Force-killing skips the LHM driver unload, leaving an `R0WinMonitor` service until reboot (harmless; `sc.exe delete` clears it).

## Architecture (big picture)

- **Composition root**: `Program.cs` → `WinMonitorContext : ApplicationContext` owns every service and window. The app lives in the tray, so there is no main form to exit from. `SyncWindow` (hidden form) is the UI-thread marshal anchor, the `TaskbarCreated` listener, and the global-hotkey receiver.
- **Data flow**: `SensorService` polls on a dedicated BelowNormal background thread and fires `SnapshotUpdated` **on that thread**; the context fans out to StatsTracker, AlertEngine, TrayIconManager, HistoryLogger and any visible form. **Every consumer marshals with a coalesced BeginInvoke (Interlocked 0/1 flag + latest-wins mailbox)** — copy that pattern for any new consumer rather than inventing another.
- **Smart polling**: with no window visible only the hardware backing tray icons and enabled alerts is updated per tick (`SetActiveSensorIds`); a full sweep runs every 30 s and Storage/Battery nodes update at most every 15 s. Nodes feeding the throttle detector are force-included.
- **Sensor identity**: LHM `Identifier` strings are the stable ids used everywhere (config overrides, tray, chart, CSV). Synthetic ids follow their own prefixes: `/wmi/thermalzone/*`, `/ec/reg/XX/Kind`, and the throttle sensor in `WellKnown`.
- **History is two-tier** (`Core/StatsTracker.cs`, `Core/SessionHistoryStore.cs`): bounded in-memory rings feed the chart via `GetHistoryIfChanged(id, knownVersion)` (version-stamped so an unchanged series copies nothing), while the *complete* session goes to an append-only fixed-width temp-file spool so CSV export covers the whole run without unbounded memory. Rings are **lazily armed** on first request and backfilled from the spool off the lock, so unplotted sensors cost nothing but a chart opened mid-session is still populated. The spool is size-capped and orphans from unclean exits are swept at startup. `ResetPeaks` clears stats and chart rings but keeps the export spool; `Dispose` closes and deletes it.
- **Diagnostics**: `Core/Diag.cs` writes a size-capped rolling `winmonitor.log` beside the config. The codebase deliberately swallows most exceptions, so this is the only record that survives sleep/crash — instrument new degradation paths there rather than adding another silent catch. The Settings → Diagnostics tab shows live polling health and opens the log.
- **EC subsystem** (LG fan support): `Core/PawnIo.cs` (P/Invoke over PawnIOLib.dll) → `Core/EmbeddedController.cs` (ACPI EC read protocol on ports 0x62/0x66, create-or-open `Global\Access_EC` mutex, per-read time budgets so a wedged EC can never stall polling — **it never writes the EC**). `Config/KnownEcProfiles.cs` ships an exact-machine default for the LG `16T90R`/gram360 (DSDT `RPM1/RPM2` at `0xB0/0xB1`, LE16 direct RPM), gated by `AppliedDefaultProfile` so a user-deleted sensor is never re-injected. **Match exact models only** — EC maps differ between closely related machines. `UI/EcExplorerForm.cs` is the register-discovery tool, retained in source but no longer surfaced in MainForm.
- **Throttle detection**: `Core/IntelThermalStatusReader.cs` reads package thermal-status MSR `0x1B1` through LHM's embedded read-only IntelMSR PawnIO module (allocation-free, reused buffers) — a real PROCHOT/thermal-status bit, not a temperature heuristic.
- **Config**: JSON via `ConfigStore` (atomic tmp+replace). Portable mode = `portable.txt` or an existing `config.json` next to the exe. Schema is at **v4**; changes go through `ConfigStore.Migrate` — bump `CurrentSchemaVersion` *and* `AppConfig.SchemaVersion` together and add a switch case. A transient read error must never nuke the config (`_loadFailed` → Save writes `config.json.recovered`).
- **SettingsForm is split into partial classes** (`SettingsForm.cs` shell + `.General/.TrayIcons/.Sensors/.Alerts/.Profiles/.Diagnostics.cs`) — put tab edits in that tab's partial. Cancel semantics: every control binds to an **isolated draft** (`_draftConfig`), so Cancel/X cannot change the live config at all. Apply/OK three-way merges baseline/draft/live so a change made elsewhere while the dialog was open survives; profiles merge by name, tray icons by sensor set (the main list's row toggle edits them live), other arrays prefer the draft. The EC Explorer opens against the draft too, so its sensor edits are undoable — `ApplySettingsCore` publishes them to the poll thread. The Diagnostics tab deliberately reads the **live service**, not the draft.
- **Theme**: `UI/Theme.cs` is the single palette source (light/dark + semantic Good/Warn/Hot). Never hardcode colors in forms or controls; read Theme at paint/build time.
- **Chart** (`UI/ChartControl.cs`): pure GDI+. Series carry their `SensorQuantity`, and quantities with incompatible units get **independent Y scales** so a 4000 RPM line cannot flatten a 45 °C line; series are additionally distinguished by marker shape (not color alone) and hovering a line labels it.
- **Tray icons**: one `NotifyIcon` per `TrayIconConfig`, GDI-rendered text at DPI-exact size. Every HICON from `IconRenderer` must be released via `IconRenderer.ReleaseIcon` (DestroyIcon) — trace any new icon path end to end.

## Hard rules

- **Lightweight is the product**: no new NuGet packages (LibreHardwareMonitorLib only), no WPF or chart libraries, and no allocations/LINQ in per-tick or per-paint hot paths (reuse buffers, cache pens/fonts, compare before formatting).
- **Localization discipline**: every user-visible string goes through `Loc.T`/`Loc.F`, and each new key must be added to **both** the `En` and `ZhTw` dictionaries in `Localization/Loc.cs`. Verify coverage after UI work — a missing key renders as the raw key, which for the primary zh-TW user means untranslated English.
- Temperatures are stored and computed in °C everywhere; °F conversion happens only at display time via `Units`.
- Sensor values may be null/NaN at any moment — render "—", never throw. A hardware node failing `Update()` on several consecutive ticks must surface null rather than stale values.
- Threading: `AppConfig` collections are UI-thread-owned. The poll thread may only read immutable snapshots handed to it explicitly (see the EC snapshot and AlertEngine patterns).
- Never commit a local `config.json`, `crash.log`, build artifacts, or unsigned replacement drivers.

## Repo state

Git remote is `bigelephantz-bot/WinMonitor2`. Work happens on `agent/*` branches; the default branch is `main` and feature branches may be ahead of it. Commit subjects are short, imperative and scoped (`Core: handle missing sensor values`). Call out new localization keys and config-schema changes in PR descriptions.
