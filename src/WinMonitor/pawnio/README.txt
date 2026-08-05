LpcACPIEC.bin — signed PawnIO module for ACPI Embedded Controller (EC) access.

Source: https://github.com/namazso/PawnIO.Modules  (release 0.2.9, file LpcACPIEC.bin)
License: LGPL-2.1-or-later (see the repository).
SHA-256: C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906

This is the OFFICIAL, digitally-signed module. It loads under the standard (signed) PawnIO
edition without enabling Windows test-signing. It exposes only two IOCTLs, restricted to the
two standard EC ports 0x62 (data) and 0x66 (status/command):
  ioctl_pio_read  (in[0]=port -> out[0]=value)
  ioctl_pio_write (in[0]=port, in[1]=value)
WinMonitor uses ONLY ioctl_pio_read plus the ACPI EC read protocol; it never writes to the EC.

Requires: PawnIO installed (https://pawnio.eu) and WinMonitor running elevated.
