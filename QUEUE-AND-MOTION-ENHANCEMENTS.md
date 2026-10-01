# Orbis Toolz 0.9.13 / PKG Relay 0.5.1

Both desktop apps now register several packages on the PS4 before waiting for any one download to finish. The console controls download and installation scheduling. This is useful for a group of DLC packages of different sizes.

## Settings

Open **Settings > RPI behavior** in either app:

- **PS4 queue limit** defaults to 4, with a range of 1–32. Set it to 1 for the previous sequential behavior. Changing it affects subsequent submissions; lowering it does not cancel already accepted tasks.
- **Progress updates** offers Default, Fast, and Realtime. Default preserves the saved polling interval. Fast allows at most two normal progress requests per second; Realtime allows at most four. The request budget is shared across the queue, so individual rows update less frequently as more tasks are monitored. Connection checks and registration/recovery commands are separate from this progress budget.
- **Default progress interval** applies when Default is selected. Its normal default is 1500 ms in Orbis Toolz and 2500 ms in PKG Relay.

Registration remains ordered and serialized. Independent downloads are monitored together, completed or failed tasks free a slot, and another Start action can add work to an existing run. DLCs and updates wait for a matching base game that is still active or waiting in the desktop queue. If that base is already installed on the PS4, its DLCs can be submitted together.

Accepted tasks recover together after restarting the app without sending another install request. All required local package routes are restored first. Sources remain available after failure so the console can retry downloads. Keep the desktop app open while the PS4 downloads local packages.

## Native WinUI motion

Navigation, settings panels, selection actions, queue empty states, and queue item insertion/removal/reordering use WinUI theme transitions and Windows Composition animations. Explicit animations check the Windows animation preference. The animations change opacity and translation without animating layout.

## Validation

The desktop builds and XAML compilation pass. The production queue, HTTP server, and RPI client are covered by 76 Orbis Toolz tests and 66 PKG Relay tests. The new checks exercise queue refill while another task remains active, sequential capacity, capacity changes, base/DLC dependencies, cancellation isolation, restart recovery, shared progress throttling, and preference persistence. PS4 responses and the UI dispatcher are simulated in these tests; package HEAD/range/manifest requests use actual HTTP servers.

Physical PS4 performance and visual animation playback have not been measured by these tests. Default polling is preserved, and the faster presets are optional. No resident payload update is required.

## Desktop builds

- Orbis Toolz: `../OrbisToolz/artifacts/OrbisToolz-0.9.13-win-x64/OrbisToolz.exe`
- PKG Relay: `artifacts/PKG-Relay-0.5.1-win-x64/app.exe`

Keep each executable with the other files in its published folder.

