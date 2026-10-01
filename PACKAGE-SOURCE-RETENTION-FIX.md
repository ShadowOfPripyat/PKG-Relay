# Keep failed PS4 download sources available

Both applications unregistered a package's HTTP route unconditionally when the
queue worker or recovery monitor exited. A PS4 install failure therefore removed
the source, causing HTTP 404 when the user resumed that download from the console.
Failed jobs did not restore their HTTP routes when the desktop app restarted.

Sources now remain available for queued failed tasks. Startup restores the exact
saved URL before monitoring active jobs, including OrbisToolz's manifest URL.
Retry preserves the URL while the job waits for submission. Older Waiting records
whose Retry action cleared the URL also register their deterministic local route
at startup, using the current PC address and HTTP port without submitting a task.
Existing saved URLs take priority; address/port/path conflicts are reported.

Completion, cancellation and explicit queue removal release sources. A route is
preserved if another queued job still uses it. Missing files and invalid saved
routes do not prevent valid sources from restoring, and invalid restoration does
not unregister another job's route.

The PS4's remaining-time sentinels (-1, 0xFFFFFFFF and 0x7FFFFFFF) are treated as
unknown, and failed jobs no longer show stale rates or remaining-time estimates.

Verification: 62 OrbisToolz tests and 52 PKG-Relay tests passed. New integration
tests execute the production queue, clients, metadata reader, persistence and
Kestrel servers. Only the PS4 responses and UI dispatch boundary are simulated.
Tests cover disk-space failure, real HTTP HEAD/range/manifest requests after
failure, restart recovery without resubmission, old retry records, shared routes,
completion/cancellation/removal cleanup, missing files and invalid saved routes.
Both Release desktop applications were rebuilt; PS4 retry with this build has
not yet been exercised on the physical console.

Close the previous desktop app, run the source-retention build, keep the package
in its queue and the app open, then retry from PS4 Downloads. No payload update is
needed. The PC address, server port and package file must remain available.
