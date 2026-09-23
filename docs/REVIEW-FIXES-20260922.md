# R1–R5 / O1–O4 implementation and validation

Date: 2026-09-22. Implementation and validation record; publication is tracked by Git and the pull request.

## Accepted fixes

| ID | Result | Regression evidence |
| --- | --- | --- |
| R1 | Complete EC configuration snapshots drive runtime publication, including Scale, BigEndian, Offset and Enabled. Failed publication can retry. | EcSettingsPublication |
| R2 | CSV uses the complete session catalog, retaining previously recorded sensors after removal/disable. | RetiredSensorExport |
| R3 | Quantity/calibration changes create new session revisions and CSV columns; live statistics/rings reset. Stable configuration IDs remain compatible. | MeasurementRevisions |
| R4 | Reset advances a spool record offset. Later chart arming cannot recover pre-reset samples, even at equal timestamps. | ResetAndObservationState |
| R5 | Unavailable EC sensors remain represented and emit explicit nulls; normal cadence skips still mean unsampled. Recovery reads immediately. | AvailabilityTransitions |

The in-app EC Explorer removal is retained. Known-profile fan monitoring and the explicitly invoked standalone SensorDump diagnostic remain supported.

## Optimizations

| ID | Implementation |
| --- | --- |
| O1 | Fake native port + real mutex exercise actual EC timeout/reset/dispose/late completion. SettingsDraft, EcSettingsPublisher, SessionExportCoordinator and DisplayMetricsSubscription expose production paths to deterministic behavior checks without hardware. |
| O2 | MainForm batch-arms the selected IDs before the chart refresh. One spool scan fills all newly requested rings, with reset/revision guards. |
| O3 | Chart-owned buffers copy only the selected time window plus one predecessor. Count bounds scaling, drawing, legend and hover; Fahrenheit conversion touches only freshly copied raw values. |
| O4 | SessionSensorInfo records measurement identity, revision and metadata; SensorObservationState separates present, available and last-observed time. Retired revisions remain immutable. Matching current labels can refresh without changing measurement semantics. |

Config remains schema v4. MeasurementKey is runtime-only and JSON-ignored. No packages added.

## Validation

- Release application and test build: warnings treated as errors; 0 warnings, 0 errors.
- Full harness: **44/44** registered groups passed (31 existing groups plus 13 new groups).
- Rendering regression uses DrawToBitmap to verify reused arrays, stale-tail exclusion, empty windows and an interpolated boundary point.
- EC timeout regression uses events, a fake port executor and an unnamed mutex; no PawnIO hardware is opened.
- Existing structural/source guards remain explicitly limited; they are not claimed as behavioral proof.
- The maintainer confirmed normal fan RPM on the physical machine after running the updated build (2026-09-22).
- CPU thermal/MSR/SMART and actual mixed-DPI taskbar behavior were not revalidated in this change.

## Performance evidence

Measured by the package-free regression harness, after buffer warm-up:

| Workload | Previous path | New path |
| --- | --- | --- |
| Arm 20 curves over a 4,000-record spool | 80,000 records across per-curve scans (code-derived comparison) | **1 scan, 4,000 records** (measured counters) |
| Read a one-minute window from a 3,600-sample ring | 3,600 values per read | **61 values**, including boundary predecessor |
| Managed allocations for history reads | **5,762,400 bytes / 100** legacy full-ring copies (measured) | **0 bytes / 1,000** warmed reusable window reads (measured) |

These measure scan work and managed allocation in the history path, not total application CPU usage or end-to-end frame time. Cold buffer growth, strings and GDI rendering can still allocate.

## Reproduce

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\tests\WinMonitor.Tests\WinMonitor.Tests.csproj -c Release --no-restore -warnaserror
& 'C:\Program Files\dotnet\dotnet.exe' .\tests\WinMonitor.Tests\bin\Release\net10.0-windows\WinMonitor.Tests.dll
```

The validated local SDK was 10.0.401. Check SDK availability afresh on other machines; do not infer it from an older environment report.
