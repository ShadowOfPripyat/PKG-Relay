# Failed duplicate BGFT task recovery

Run artifacts/PKG-Relay-bgft-recovery-win-x64/app.exe, retry the failed package, and start the queue.
The installed 0.9.12-resident5 PS4 payload can remain running; this fix is in the
desktop client's sending logic.

The real console log showed successful service initialization followed by
sceBgftServiceIntDownloadRegisterTask returning 0x80990015. Read-only queries
found matching Trailmakers BGFT task 386, subtype 6, with error 0x80431068 and
zero transferred bytes. The old failed task blocked a new registration.

When registration reports a duplicate, the client finds the task for the exact
content ID and package subtype on the same endpoint and reads its progress.
Only a confirmed failed task is unregistered. The client then retries once with
the current URLs. Healthy, paused, completed, unknown, and unconfirmed tasks are
left alone. Failed or unconfirmed cleanup stops recovery.

The same implementation is applied to PKG-Relay's legacy installer client.
Legacy HTTP 200 responses with status=error are now recognized as failures too.

Verification: 44 OrbisToolz tests and 34 PKG-Relay tests passed, including actual
HTTP calls through both production clients. These cover resident and legacy
responses, signed/hex error codes, failed duplicate recovery, active/unknown
task preservation, package subtype, cleanup failure, bounded retries, and
cancellation. Both desktop applications were rebuilt. Console queries confirmed
the diagnosis; a complete installation with this new desktop build has not yet
been performed.
