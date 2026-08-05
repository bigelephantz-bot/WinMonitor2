# Third-Party Notices

WinMonitor's original source code is Copyright (c) 2026 Michael Lin and is
licensed under the MIT License in `LICENSE`. The notices below cover third-party
components redistributed with WinMonitor. They do not change those components'
licenses, and the WinMonitor license does not replace their terms.

## Redistributed Components

| Component | Version | License | Corresponding source |
|---|---:|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | [Source commit 3d331e3](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701) |
| BlackSharp.Core | 1.0.7 | MPL-2.0 | [Source commit c70b735](https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0) |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | [Source commit 25319ea](https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea) |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | [Source commit 3b47b96](https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce) |
| HidSharp | 2.6.4 | Apache-2.0 | [NuGet package and project link](https://www.nuget.org/packages/HidSharp/2.6.4) |
| Mono.Posix.NETStandard, including MonoPosixHelper native binaries | 1.0.0 | Upstream composite MIT/BSD and component-specific terms | [Package license URL](https://go.microsoft.com/fwlink/?linkid=869050) |
| System.IO.Ports | 10.0.3 | MIT | [Source commit c2435c3](https://github.com/dotnet/dotnet/tree/c2435c3e0f46de784341ac3ed62863ce77e117b4) |
| System.Management | 10.0.2 | MIT | [Source commit 4452502](https://github.com/dotnet/dotnet/tree/44525024595742ebe09023abe709df51de65009b) |

The MPL-2.0 components are redistributed as unmodified package binaries.
Their source remains available at the exact revisions linked above. The full
MPL-2.0 text is in `licenses/MPL-2.0.txt`.

## PawnIO Modules

LibreHardwareMonitorLib 0.9.6 embeds PawnIO.Modules 0.2.2 binaries for low-level
hardware access: `AMDFamily0F`, `AMDFamily10`, `AMDFamily17`, `IntelMSR`,
`IsaBridgeEC`, `LpcACPIEC`, `LpcCrOSEC`, `LpcIO`, `RyzenSMU`, `SmbusI801`,
`SmbusNCT6793`, and `SmbusPIIX4`. WinMonitor also redistributes the official,
unmodified `LpcACPIEC.bin` from PawnIO.Modules 0.2.9 for its read-only LG EC
integration.

PawnIO.Modules is licensed under LGPL-2.1-or-later. Complete corresponding
source is available from the official releases:

- [PawnIO.Modules 0.2.2 source](https://github.com/namazso/PawnIO.Modules/archive/refs/tags/0.2.2.zip)
- [PawnIO.Modules 0.2.9 source](https://github.com/namazso/PawnIO.Modules/archive/refs/tags/0.2.9.zip)

The complete LGPL-2.1 license and upstream PawnIO notice are reproduced in
`licenses/LIBREHARDWAREMONITOR-THIRD-PARTY-NOTICES.txt`. PawnIO's installed
driver and `PawnIOLib.dll` are optional system prerequisites and are not
redistributed by WinMonitor.

## Included License Texts

- `licenses/MPL-2.0.txt`: MPL-2.0 components listed above.
- `licenses/Apache-2.0.txt`: HidSharp.
- `licenses/DOTNET-LICENSE.txt`: Microsoft .NET package binaries.
- `licenses/DOTNET-THIRD-PARTY-NOTICES.txt`: notices supplied with the .NET packages.
- `licenses/MONO-LICENSE.txt`: composite upstream Mono licensing terms.
- `licenses/MONO-PATENTS.txt`: Microsoft patent promise referenced by the Mono license.
- `licenses/LIBREHARDWAREMONITOR-THIRD-PARTY-NOTICES.txt`: upstream notices,
  including BSD terms for Aga.Controls and the complete LGPL-2.1 terms for
  PawnIO.Modules.

All source links above were selected from the resolved NuGet metadata or the
upstream release used to produce the redistributed binary. Preserve this file,
`LICENSE`, and the `licenses` directory when redistributing WinMonitor.
