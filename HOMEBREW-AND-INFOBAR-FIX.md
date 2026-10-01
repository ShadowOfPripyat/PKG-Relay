# Homebrew and InfoBar fixes — Orbis Toolz 0.9.14 / PKG Relay 0.5.2

The configured PKG-Zone root returned HTTP 403 with an HTML browser verification page. Its catalog endpoint was also blocked, and neither app's old generic JSON parser could load that site as a catalog. The apps now recognize this legacy root and show a catalog assembled from official project release feeds. Custom catalog URLs remain supported; the provider shown above the list identifies the current source.

## Homebrew

- Leave the Catalog API URL empty under Settings > Homebrew for official project releases. Existing `http://api.pkg-zone.com/` and `https://api.pkg-zone.com/` root settings also use these feeds, with an explanatory notice. This does not reproduce the complete PKG-Zone library.
- Public JSON catalogs support arrays, nested items/packages/data containers, PKG-Zone field names, and FPKGi URL-keyed DATA dictionaries. HTTP/HTTPS package links are validated, relative URLs are resolved, and duplicate URLs are removed.
- Search names, descriptions, authors, categories, title IDs, content IDs, and versions. Combine this with a category filter and sort by name, release date, or size. Unknown sizes sort last in the smallest-first view. ISO and day-month-year release dates are supported.
- The three view buttons select a detailed list, larger grid cards, or compact tiles. View, category, and sorting preferences are saved. The controls use native WinUI elements and retain the current search when switching views.
- Refreshes retain a provider-specific cached catalog during an outage. Errors describe browser challenges, unsupported webpage responses, and unavailable feeds. Cache data is never reused for a different custom URL.
- Install downloads a local PKG to the configured Homebrew folder, adds it to Packages, and starts the queue. Download to PC adds it without an explicit queue start; the global automatic-start setting still applies. Downloads close their file before renaming it on Windows, validate the PKG header, and remove incomplete files after an error or cancellation.

The live smoke check loaded six PS4 packages: Apollo Save Tool, GoldHEN Cheat Manager, PS4 WebDAV Client, PS4 EZ Remote Client, PS4 FTP Client, and Payload Guest. It also fetched the beginning of a release asset and verified the PS4 header `7F434E54`. Some projects publish no directly downloadable PS4 PKG asset and are omitted. Feed contents can change over time.

Official sources include [Apollo](https://github.com/bucanero/apollo-ps4/releases), [GoldHEN Cheat Manager](https://github.com/GoldHEN/GoldHEN_Cheat_Manager/releases), [WebDAV Client](https://github.com/cy33hc/ps4-webdav-client/releases), [EZ Remote Client](https://github.com/cy33hc/ps4-ezremote-client/releases), [FTP Client](https://github.com/cy33hc/ps4-ftp-client/releases), and [Payload Guest](https://github.com/Al-Azif/ps4-payload-guest/releases).

## InfoBars in both apps

Every InfoBar opts into the shared native Windows Composition behavior:

- Opening: slide down 14 pixels and fade in over 220 ms.
- Closing or programmatic disappearance: slide up and fade out over 160 ms, then allow the native InfoBar to finish closing.
- Title or severity changes while already visible: a separate, gentle opacity pulse. Percentage message updates stay steady.

The behavior listens to InfoBar's Closing event because IsOpen hides an internal template child; attaching an implicit fade only to the outer control does not preserve a visible exit. A new message received during an exit cancels that pending exit. Page unload resets animations, and explicit animations respect the Windows animation preference. The close handling uses the control's documented [cancellable Closing behavior](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar).

## Validation and builds

All 186 regression tests pass (98 Orbis Toolz, 88 PKG Relay). The headless suites link the production catalog loader, parser, query logic, downloader, settings, queue, server, and RPI client. They cover provider failures/cache separation, real Windows file writes and rename, invalid downloads and cleanup, cancellation, filtering/sorting, and preference persistence in addition to the package-sending regressions. WinUI Debug and Release builds, including XAML compilation, also succeed in both apps. Visual animation playback has not been inspected in these automated checks.

- Orbis Toolz: `../OrbisToolz/artifacts/OrbisToolz-0.9.14-win-x64/OrbisToolz.exe`
- PKG Relay: `artifacts/PKG-Relay-0.5.2-win-x64/app.exe`

Keep each executable with the other files in its published folder. No PS4 resident payload update is required.

If a Debug build reports `TextBox`/`AutoSuggestBox` conversion errors in `HomebrewPage.g.cs`, its generated XAML connector is stale. The current XAML and handler both use `AutoSuggestBox`. Stop the build, remove the application's `obj/x64/Debug` folder, then rebuild Debug x64; do not edit generated `.g.cs` files. Regenerating these intermediates resolved the reproduced errors.

The optional live check is `dotnet run --project ../OrbisToolz/scripts/HomebrewSmoke/HomebrewSmoke.csproj -c Release`. It reads public feeds and a package header, and uses a temporary data directory without touching the user's settings or submitting any PS4 tasks.

