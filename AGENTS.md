# Repository Guidelines

## Project Structure & Module Organization

The main .NET 10 WinForms application lives in `src/WinMonitor`. Keep code within the existing namespaces and folders: `Core` for sensor polling and statistics, `Config` for persisted settings, `Tray` for notification-area behavior, `UI` for forms and controls, and `Localization` for user-facing text. `tools/SensorDump` is a console diagnostic that references the main project. Hardware notes belong in `docs`; `dist` is generated publish output and should not be edited manually. Read `ARCHITECTURE.md` before changing module boundaries, polling, EC access, or UI threading. If you were asked to review this repository, read `docs/REVIEW_BRIEF.md` — it scopes the review and lists the deliberate decisions that must not be "fixed".

## Build, Test, and Development Commands

- `dotnet build .\src\WinMonitor\WinMonitor.csproj -c Debug`: restore dependencies and compile the app.
- `dotnet run --project .\src\WinMonitor\WinMonitor.csproj`: launch a development build.
- `dotnet run --project .\tools\SensorDump\SensorDump.csproj`: print detected sensors for diagnostic validation.
- `dotnet run --project .\tests\WinMonitor.Tests\WinMonitor.Tests.csproj -c Release`: run the package-free regression harness.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1`: create installed and portable Release builds under `dist`.

Use Windows 10/11 x64 with the .NET 10 SDK. Run elevated when validating CPU, SMART, or fan access.

**On the maintainer's machine the `dotnet` on `PATH` is runtime-only — `dotnet --list-sdks` prints
nothing and every command above fails.** Use the user-local SDK instead, substituting it for
`dotnet` in each command:

```powershell
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build .\src\WinMonitor\WinMonitor.csproj -c Debug
```

`publish.ps1` already resolves an SDK on its own (system, then user-local, then a workspace SDK
under `%TEMP%\dotnet-sdk-*`), so it runs as written.

The regression harness has **no per-test filter**: it runs a hardcoded array in
`CoreRegressionTests.cs`. To run one check in isolation, temporarily trim that array.

To smoke-test without a UAC prompt from a non-elevated shell (the manifest is `highestAvailable`):

```powershell
$env:__COMPAT_LAYER = "RunAsInvoker"
Start-Process .\src\WinMonitor\bin\Debug\net10.0-windows\WinMonitor.exe
```

Such a run is degraded by design — battery and CPU load only. Force-killing the app skips the
LibreHardwareMonitor driver unload and leaves an `R0WinMonitor` service until reboot (harmless;
`sc.exe delete R0WinMonitor` clears it).

## Coding Style & Naming Conventions

Use C# 12, nullable reference types, implicit usings, four-space indentation, file-scoped namespaces, and braces on new lines. Name types, methods, and properties in `PascalCase`; locals and parameters in `camelCase`; private fields as `_camelCase`. Split large WinForms partial classes by concern, following `SettingsForm.Alerts.cs`.

Keep comments in English. Route every visible string through `Loc.T("key")` and add both English and `zh-TW` entries. Store temperatures in Celsius and convert only for display. Avoid allocations in polling paths, marshal background sensor events to the UI thread, and dispose GDI/native handles.

## Testing Guidelines

The package-free regression harness lives in `tests/WinMonitor.Tests`; it covers core configuration, history, alert, and CSV behavior. Run it before publishing; `publish.ps1` runs it by default and accepts `-SkipTests` only for an intentional bypass. No coverage threshold exists. Manually exercise affected tray, settings, logging, localization, and sensor behavior; use `SensorDump` for hardware-facing changes. Name new tests `<Type>Tests.cs` with behavior-focused names such as `Load_CorruptConfig_UsesDefaults`.

## Commit & Pull Request Guidelines

Use short, imperative, scoped subjects, matching the existing history — for example `Core: handle missing sensor values`. Work happens on `agent/*` branches; `main` is the default branch. Pull requests should explain the behavior change, list validation commands and hardware/admin conditions, link relevant issues, and include screenshots for UI changes. Call out new localization keys and configuration-schema changes. Never commit local `config.json`, `crash.log`, build artifacts, secrets, or unsigned replacement drivers.
