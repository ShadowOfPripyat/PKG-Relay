# Homebrew catalog — Orbis Toolz 0.9.16 / PKG Relay 0.5.4

Homebrew now groups package variants into one app. Each layout has a version dropdown; Install and Download use the selected package, and its firmware, date, size and source update together. Stable official project releases are preferred. Release dates order published releases, with numeric version ordering when dates are unavailable; this also handles projects that changed their numbering format. Prereleases remain selectable.

The official GitHub feeds include Apollo Save Tool, GoldHEN Cheat Manager, Payload Guest, the cy33hc clients, FPKGi and Wiliwili. PS4 Xplorer 2.0, PS4 Temperature and Remote Package Installer also have store download entries. The Lapy firmware ranges reflect the developer's published listing, not a guarantee for later firmware. Unpublished firmware/date/version information is explicitly marked as unavailable. Store entries and official releases merge into the same app where identities match.

Grid cards use large artwork with a compact footer. Tile view uses horizontal cards with small artwork and an actions menu. List view keeps full metadata and direct buttons. First loading uses native WinUI placeholder controls animated with Windows Composition; motion follows the Windows animation preference and stops when loading ends or the page unloads. Images retain the typed converter that fixes missing-artwork crashes.

## Full catalog and browser verification

Select **Connect full catalog**. The embedded WebView2 browser opens the provider's `store.db` download; complete any verification normally. The downloaded SQLite catalog is read without modifying it, saved locally, and merged with official releases. A saved full catalog remains available when GitHub cannot refresh, including before any official-feed cache exists. Select Connect full catalog again to update the store snapshot.

If WebView2 is missing or fails to initialize, the dialog provides Microsoft's runtime download link and an external-browser link with instructions. Download `store.db` in your browser, then select **Import catalog**. Public JSON catalogs can also be imported. A failed import preserves the prior catalog. Store access still depends on the provider accepting the browser verification; this does not automatically solve or circumvent its challenge.

Protected package downloads similarly open the browser after HTTP 403 or an HTML response. Without a working embedded browser, download the PKG externally and use **Packages → Add PKG**. Browser-downloaded packages are validated before entering the library, and incomplete downloads are canceled and removed when the dialog closes. GitHub downloads continue to use the existing direct path.

## FPKGi Homebrew database

Select **FPKGi database** to create `HOMEBREW.json` from each app's currently selected package and open its folder with setup instructions. Install/open FPKGi once, then copy the file over FTP to `/user/data/FPKGi/ContentJSONs/HOMEBREW.json`. Enable FPKGi's Direct Download option for GitHub packages. The export uses URL-keyed DATA entries, release dates, artwork and a numeric minimum firmware where supplied. It exports the currently available Homebrew catalog; connect/import the full catalog first for additional apps.

## Verification

Both applications build in Debug and Release. Core regression tests cover grouping/version formats, firmware metadata, read-only SQLite import, invalid-import preservation, offline catalog recovery, FPKGi export and validation of browser-downloaded PKGs alongside existing queue/sending behavior.

The native `scripts/Test-HomebrewUi.ps1` harness loads the actual page and exercises skeleton visibility, a real version dropdown, search/filter/sort, all layouts, unload/reload and InfoBar transitions. With WebView2 installed, it also downloads a SQLite catalog from an isolated local HTTP server through the actual browser and imports it. Browser subprocess creation requires running outside the Codex process sandbox. The local test validates browser/download wiring; it does not certify that the external provider will accept its verification on every PC. No PS4 is contacted.

A live official-feed check returned 11 apps and 111 package variants and verified the PS4 PKG signature from a current release asset. No console payload update is required. Run each portable executable with the other files in its release folder.
