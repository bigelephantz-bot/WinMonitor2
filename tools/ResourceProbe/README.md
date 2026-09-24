# WinMonitor ResourceProbe

Runs the real WinMonitor polling and WinForms UI in an isolated measurement host.
The app's public constructor keeps normal behavior; only this friend assembly can opt out of startup
registration, global hotkeys and the activation pipe. Config/log writes go to a unique temporary directory.

## Run

```powershell
dotnet build tools/ResourceProbe/ResourceProbe.csproj -c Release -warnaserror
dotnet tools/ResourceProbe/bin/Release/net10.0-windows/WinMonitor.ResourceProbe.dll --seconds 30 --warmup 10 --out dist/resource-probe/run.json
```

Allow about 6 minutes. Close other WinMonitor instances and leave the test windows alone.
The tool shows/ closes real windows and selects up to four reporting sensors without changing personal settings.
It shuts down polling normally; do not force-kill it during native hardware access.

## Method

- Four states: tray only, main without chart, main with chart, Diagnostics.
- Two rounds in opposite order; 20-second startup warmup, per-state warmup and 1-second sampling.
- CPU is process CPU time divided by elapsed time and logical processor count. Memory, GDI/USER handles,
  allocation rate, GC collections and poll counts are captured.
- Runtime settings match WinMonitor: workstation GC, Concurrent=false, ConserveMemory=5, TieredPGO=true.
  Verify generated runtimeconfig files if app settings change.
- No forced GC during measurements. Optional `--retention-check` runs a separate forced-collection
  diagnostic only after all timings. This is not a recommended production GC policy.

The output must not already exist. Raw JSON includes environmental conditions but omits sensor IDs,
machine/user names and personal config. Temporary isolated config/logs are retained for diagnosis.

Measurements include sampling overhead and process caches carried between phases. Non-elevated results
cannot establish the cost of elevated EC/MSR/SMART polling; this is not a before/after CPU benchmark.
