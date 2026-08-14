# Review brief

Instructions for an agent asked to review this repository. Read `ARCHITECTURE.md` first — it is the
binding module contract, not a description.

## How to run the review

Review in passes, one theme at a time, on its own branch. A single sweep over ~14k lines produces a
long list of shallow findings; a scoped pass produces few findings that are actually true.

**Pass 1 must be findings only — no code changes.** Report first, get the list triaged, then
implement what was accepted. A review that arrives as a large diff cannot be separated from the
review itself.

| Pass | Scope | What matters here |
|---|---|---|
| 1. Threading & lifetime | `Core/SensorService.cs`, `Core/StatsTracker.cs`, `Core/SessionHistoryStore.cs`, `Program.cs`, `Tray/TrayIconManager.cs` | Races between the poll thread and the UI thread, dispose ordering, behavior across sleep/resume, unbounded growth |
| 2. Native boundary | `Core/PawnIo.cs`, `Core/EmbeddedController.cs`, `Core/IntelThermalStatusReader.cs`, `Tray/IconRenderer.cs` | P/Invoke signatures and marshaling, handle and HICON lifetime, mutex acquisition, time budgets on a wedged EC |
| 3. Persistence | `Config/ConfigStore.cs`, `Config/AppConfig.cs`, `Core/HistoryLogger.cs`, CSV export | Migration chain v1→v4 (every case, including skipped versions), atomic write, corrupt-input handling, retention sweep |
| 4. UI | `UI/MainForm.cs`, `UI/SettingsForm*.cs`, `UI/ChartControl.cs`, `UI/FlyoutForm.cs`, `UI/CompactForm.cs` | Per-monitor DPI, theme application, control disposal, null/NaN sensor values reaching a formatter |
| 5. Cross-cutting | whole tree | Per-tick and per-paint allocations, dead code, drift between `ARCHITECTURE.md` and the code |

## Required finding format

For each finding:

- **Location** — `path/file.cs:line`
- **Severity** — crash / data loss / wrong reading / leak / perf / style
- **Evidence** — the specific code path that produces the failure, not a general concern
- **Reproduction or verification** — how to demonstrate it, or explicitly "needs hardware" (see below)
- **Confidence** — high / medium / low

A finding whose evidence is "this pattern is usually wrong" is a low-confidence finding and should
say so. Prefer five findings with traced call paths to thirty pattern matches.

## Deliberate decisions — do not "fix" these

Each of these looks like a defect and is not. Changing one is a regression.

1. **Tray icons draw no outline or halo, digits only, at most 3 glyphs, no unit.** This was derived
   by rendering candidates against real light and dark taskbars. An 8-way halo composites into a
   near-opaque ring that closes the counters of `8`, `6`, `0`; 4+ glyphs in a 16px canvas is
   unreadable at any hinting. Below 13px, whole-pixel hinting beats antialiasing.
2. **Most exceptions are swallowed on purpose.** A sensor backend failing must degrade, never
   surface a dialog. The correct change to a swallowing path is to add a `Diag.Log` breadcrumb, not
   a rethrow.
2b. **A native object an abandoned call may still be using is kept alive on purpose.** Not disposing
   it is not enough — dropping the last managed reference lets a finalizer close the same handle the
   call is inside. `EmbeddedController` pins those objects for the process lifetime; that list is
   not a leak to tidy up. See `NativeCallGate` and `PollThreadHandle` for the same rule at their
   own boundaries.
3. **Chart history backfill runs on a worker thread and can be discarded.** The reset-generation
   check in `StatsTracker` is the guard against a peak reset landing mid-backfill — not a leftover.
4. **`EmbeddedController` never writes the EC.** Read-only is a safety boundary, not an omission.
5. **`KnownEcProfiles` matches exact machine models only.** EC register maps differ between closely
   related models; a loosened match writes wrong sensors onto other people's laptops.
   `AppliedDefaultProfile` exists so a user-deleted sensor is never re-injected.
6. **No new NuGet packages** (LibreHardwareMonitorLib only), no WPF, no chart library. Lightweight
   is the product, not an implementation detail.
7. **No allocations or LINQ in per-tick or per-paint paths.** Reuse buffers, cache pens and fonts,
   compare before formatting.
8. **`SettingsForm` edits an isolated draft**, never the live config, so Cancel and X are no-ops by
   construction. Apply/OK three-way merges only the draft's own changes into the current live
   config (baseline / draft / live), which is what lets a tray action taken elsewhere survive an
   Apply. Named profiles merge by name and tray icons by their sensor set; other arrays prefer the
   draft. The Diagnostics tab deliberately reads the **live service**, not the draft, because
   polling health must stay accurate while edits are pending.
9. **A non-elevated run is degraded by design** — battery and CPU load only.
10. **Temperatures are °C everywhere internally**; °F exists only at display time.
11. **`SensorService` fires `SnapshotUpdated` on the poll thread.** Consumers marshal with a
    coalesced `BeginInvoke` (Interlocked 0/1 flag + latest-wins mailbox). New consumers copy that
    pattern rather than inventing another.
12. **A config read error must never overwrite the config** — `_loadFailed` makes Save write
    `config.json.recovered` instead.

If a pass concludes one of these is genuinely wrong, say so explicitly with evidence and leave it
unchanged for a decision.

## What cannot be verified without the target hardware

CI and any non-elevated run cannot exercise these. Mark findings in these areas as needing hardware
confirmation rather than asserting behavior:

- CPU package temperatures and per-core sensors (needs elevation)
- MSR `0x1B1` throttle detection (needs elevation + Intel CPU)
- EC fan RPM via PawnIO (needs elevation + PawnIO installed + an LG `16T90R`/gram360)
- NVMe SMART health and TBW (needs elevation)
- Tray icon legibility (needs a real taskbar at the user's DPI)

The reference machine is an LG gram 16T90R, Intel i7-1360P, Windows 11.

## Definition of done for an accepted fix

- Builds clean with `-warnaserror`
- Regression harness passes; a behavior fix adds a check to `tests/WinMonitor.Tests/CoreRegressionTests.cs`
- New user-visible strings exist in **both** the `En` and `ZhTw` tables in `Localization/Loc.cs`
  (the localization coverage check enforces this)
- A config schema change bumps `ConfigStore.CurrentSchemaVersion` and `AppConfig.SchemaVersion`
  together and adds a migration case
- `ARCHITECTURE.md` updated if a module contract moved
- One theme per branch, so a bad batch can be dropped without unpicking a good one
