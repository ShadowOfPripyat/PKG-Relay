# Status badges and Orbis Toolz icon

Orbis Toolz 0.9.18 / PKG Relay 0.5.6

Orbis package badges previously used an implicit OneTime background binding. The
label changed when the install job advanced, but the background retained its
initial waiting color. Both apps now use the same reusable native WinUI badge,
with explicit OneWay compiled bindings for background, foreground and label.
Replacing the associated job also replaces the property-change subscriptions.

Colors: waiting/cancelled gray; preparation, sending, recovery and installing
blue; paused amber with black text; finalizing/completed green; failure red.
The palette matches PKG Relay's existing status colors.

Orbis dashboard service rows show green Available and red Unavailable badges.
The console and GoldHEN summaries use the same colors; unconfigured/unknown
states remain gray. A failed scan clears stale service rows and marks the
summary unknown. Live availability changes notify both color and label bindings.

Orbis Toolz has a cyan orbit and white package icon, embedded in the executable
and used by the native window/taskbar and custom title bar. The ICO contains
16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel images. The SVG master and PNGs are
in `src/RemotePkgSender/Assets/Branding`; regenerate with `scripts/New-AppIcon.ps1`
using PowerShell 7 on Windows.

Native WinUI regression checks use the actual production badge. They exercise
every status, paused-text contrast, job replacement, old-job detachment and
updates to the new job. Orbis additionally checks dashboard rows, summary
colors, unknown/unconfigured states, window icon and title-bar PNG loading.
