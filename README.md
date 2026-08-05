# WinMonitor

[English](README.md) | [繁體中文](README.zh-TW.md)

WinMonitor is a lightweight Windows hardware monitor built with .NET 10 WinForms. It combines live sensor readings, configurable system-tray icons, independent-scale charts, alerts, and CSV history in a compact desktop application.

## Highlights

- Monitors supported CPU, GPU, storage, memory, battery, motherboard, and fan sensors through LibreHardwareMonitor.
- Shows temperature, power, clock, load, voltage, data, PWM, and RPM values with session minimum, maximum, and average statistics.
- Displays configurable tray icons with units, threshold colors, multi-sensor rotation, and compact-mode access.
- Charts selected sensors over 1, 3, 5, 10, 20, 30, or 60 minutes. Quantity groups use independent Y axes, distinct markers, and hover labels.
- Provides threshold alerts, profiles, automatic startup, adaptive polling, daily peak reset, and English/Traditional Chinese UI.
- Exports every recorded sample from the current application session as a time-series CSV. Optional background logging writes daily CSV files.
- Reports CPU thermal-throttling state when supported, with a temperature-based fallback when direct MSR status is unavailable.
- Includes read-only ACPI EC support for known LG gram fan-speed registers. WinMonitor never writes EC registers.

## Requirements

- Windows 10 or Windows 11, x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run a framework-dependent build
- .NET 10 SDK to build from source
- Administrator privileges for the widest CPU, NVMe SMART, and EC sensor access
- [PawnIO 2.2.0 or later](https://github.com/namazso/PawnIO.Setup/releases) for supported privileged and EC telemetry

## Run from Source

```powershell
dotnet run --project .\src\WinMonitor\WinMonitor.csproj
```

For hardware diagnostics, run the sensor inventory tool from an elevated terminal:

```powershell
dotnet run --project .\tools\SensorDump\SensorDump.csproj
```

## Build, Test, and Publish

```powershell
dotnet build .\src\WinMonitor\WinMonitor.csproj -c Release
dotnet run --project .\tests\WinMonitor.Tests\WinMonitor.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

`publish.ps1` creates installed and portable builds under `dist\`. The portable build stores configuration beside the executable; the installed build uses `%AppData%\WinMonitor`.

## Project Layout

| Path | Purpose |
|---|---|
| `src/WinMonitor` | WinForms application, sensor services, UI, tray integration, and configuration |
| `tests/WinMonitor.Tests` | Package-free regression harness |
| `tools/SensorDump` | Elevated sensor and EC diagnostic utility |
| `docs` | Hardware-specific notes and fan telemetry guidance |
| `ARCHITECTURE.md` | Module contracts, threading rules, and data flow |

## Hardware Notes

Sensor availability depends on the hardware, firmware, privileges, and driver version. The bundled LG fan mapping currently targets known `16T90R`/gram 360 firmware fields; other systems may require a new read-only profile. Use `SensorDump` before reporting missing telemetry.

## Licensing

No project-wide license file is currently included. Third-party components retain their own licenses, including LibreHardwareMonitorLib (MPL-2.0) and the PawnIO EC module (LGPL-2.1-or-later). Review those terms before redistribution.
