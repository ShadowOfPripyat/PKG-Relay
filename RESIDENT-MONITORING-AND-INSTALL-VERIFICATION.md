# Resident monitoring and install verification

Orbis Toolz 0.9.19 / PKG Relay 0.5.7 / PS4 payload 0.9.19-resident6

## Update the PS4 payload

After updating Orbis Toolz, open **Tools → Orbis Toolz resident install service →
Install + Start**. This rebuilds and replaces the PS4 payload. Updating the
desktop executable alone does not replace the running PS4 service.
The Tools action now stops the running service before staging the replacement;
previously it could report success while still using the old in-memory payload.

## Service survival and routing

The old payload tried to return an HTTP error after a bare TCP probe disconnected.
Writing to that closed socket could raise SIGPIPE and terminate the service.
The daemon now ignores empty/incomplete requests, suppresses SIGPIPE on response
writes, stops after a failed header write, and bounds request receive/send waits.
Orbis dashboard discovery now uses the resident HTTP health endpoint.

Task commands and progress queries try the service that registered/found the task
first. A cached negative health result no longer excludes the resident service
from actual task requests. Transport failures preserve the errors from both
endpoints, rather than showing only the final legacy connection refusal.

## Confirm an installed package

The new authenticated `/api/get_install_status` endpoint checks the registered
application, whether the content is still installing, and its installed PKG
header. The full content ID and nonzero package digest must match the local PKG.
Base packages, patches and DLC use their respective installed locations on
internal storage and supported external mounts. An installed base game alone
cannot confirm a DLC or a different update.

Both apps check active jobs periodically and revisit submitted failed jobs every
30 seconds, checking at most four failed jobs per pass. A confirmed console-side
retry becomes Completed without resending the package. The per-package menu also
offers **Check installed status**. Completion clears stale progress/ETA values
and releases the download route only when no other job needs it.

Unavailable/older payloads, missing local metadata, inaccessible storage layouts
and remote URL jobs cannot provide this confirmation. They keep their existing
queue state and download source. Keep the PC application open and the local PKG
available when retrying from the PS4 Downloads screen.

PKG Relay can reuse the same Windows user's Orbis Toolz pairing automatically
when the configured PS4 address matches. Alternatively, its Settings page has
optional resident verification port/token fields. PKG Relay still submits
installs through its legacy installer endpoint.

## Validation

- 134 Orbis core tests and 119 PKG Relay core tests passed, including endpoint
  routing, exact-identity verification, failed-download source lifetime and
  reconciliation without resubmission.
- 10 native payload host checks passed, including a real loopback TCP disconnect,
  failed response writes, and DLC identity/path/active-install checks.
- The payload compiled and linked for the PS4 target with Payload SDK v0.9 and
  clang/lld 18.1.8. Both desktop applications published successfully.

The checks use mocked PS4 imports; a physical PS4 on firmware 13.02 still needs
to verify the new payload and installed-storage queries.

The AppInstUtil function declarations were checked against the
[OpenOrbis header](https://github.com/OpenOrbis/OpenOrbis-PS4-Toolchain/blob/master/include/orbis/AppInstUtil.h).
