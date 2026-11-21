# ANCS Reader

All commands and paths below are relative to the repository root.

Independent iPhone ANCS reader

Runtime: dist/ancs-reader/AncsReader.exe, self-contained win-x64 .NET 10, no installed SDK required.
Build from source: install .NET 10 SDK, stop the receiver, run ./scripts/build.ps1 build.
Devices: ./scripts/build.ps1 devices
Self-test: ./scripts/build.ps1 selftest
Listen: ./scripts/build.ps1 listen -DeviceId '<paired BLE id>' -Seconds 120
Continuous mode: -Seconds 0. The C# daemon normally owns this process.

Uses Windows public BLE/GATT APIs and Apple ANCS. No Phone Link dependencies.
No SMS sending, notification dismissals, pairing changes or mark-read commands.

The daemon sets ANCS_PROBE_HISTORY=1 to enable replay of existing notifications.
The reader drains live events ahead of replay events. The daemon deduplicates by
content and accepts timestamps from five seconds before receiver supervision
started onward, up to 120 seconds old and up to 30 seconds ahead of the PC clock.
ANCS_PARENT_PID makes the reader exit if its owning process disappears.
Service changes, disconnects, queue loss or response timeout fail the session;
The daemon recreates the process with capped exponential backoff and a heartbeat watchdog.

JSONL carries plaintext SMS bodies only for com.apple.MobileSMS; other apps emit IDs.
ANCS notifications are not a full SMS archive. Missing/cleared notifications are not
recoverable by this protocol. No guarantee of delivery during long radio outages.

Tests on 2026-10-13: locked-phone new SMS -> ANCS -> OTP extraction -> clipboard
readback matched. Child-process forced exit recovered without duplicate code records.
Device listing, explicit disconnect (child stopped) and reconnect API passed.
See tests/Program.cs for regressions; the parser includes a self-test.

On 2026-11-18, an iPhone received a new SMS code. The receipt window displayed
the code, the Windows clipboard matched it, and the daemon's code count went
from 0 to 1. Disconnecting stopped the reader. Reconnecting the same iPhone
started a new reader and restored its heartbeat. During the next 12 seconds,
the count stayed at 1, the latest receipt stayed the same, and the Windows
clipboard sequence number did not change.

Sources:
https://developer.apple.com/library/archive/documentation/CoreBluetooth/Reference/AppleNotificationCenterServiceSpecification/Specification/Specification.html
