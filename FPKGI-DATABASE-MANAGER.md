# FPKGi database manager — Orbis Toolz 0.9.17 / PKG Relay 0.5.5

FPKGi content management is now under **Tools**. Orbis Toolz has a **FPKGi databases** item in its Tools sidebar. PKG Relay has a Tools tab containing the same local database manager. The former Homebrew-tab export button has moved into this manager.

The downloader defaults to the user's supplied sources:

- [GAMES.json](https://archive.org/download/ps4-fpkg-collection-english-fpkgi/GAMES.json)
- [DLC.json](https://archive.org/download/ps4-fpkg-collection-english-fpkgi/DLC.json)

Games and DLC are selected initially. Click **Download / update Games and DLC** to fetch the selected types. This is a downloader rather than a bundled snapshot, so the provider's current files are used. The application downloads catalog JSON; package installation remains a separate action.

The tool also provides:

- Per-database entry count, size and saved date.
- Import of JSON or `.bak` files into a chosen Games, DLC or Homebrew slot.
- Validation of selected local files.
- Automatic previous-version backups and restore of a previous local version.
- Homebrew database generation from the current catalog's latest stable versions.
- Editable download URLs, saved preferences and restoration of default Archive.org sources.
- Opening the managed database folder with transfer instructions.
- Progress and a Cancel button near the top of the tool.

Downloads enforce a 64 MB limit and validate FPKGi's URL-keyed DATA structure. The entire selected download set is staged and validated before working files are replaced. Invalid responses and cancellation leave existing databases intact and remove partial downloads. Imports also validate before replacement and back up the previous file. Database types are managed separately.

## Sending to the console

Open FPKGi once to create its data folders, then close it. Enable GoldHEN's FTP server and configure the console address/FTP port in Orbis Toolz. Choose the desired databases and click **Send selected databases to PS4**. The destination is `/user/data/FPKGi/ContentJSONs/`, using the standard GAMES.json, DLC.json and HOMEBREW.json filenames. Reopen FPKGi afterward. Use FPKGi's Direct Download option for GitHub packages.

Orbis Toolz saves existing remote database text under the local manager's `PS4-backups` folder. Uploads use a unique temporary remote filename and rename it only after transfer completes; failed renames leave the existing working file in place. The staged upload, rename and cleanup retain their original console connection, and console changes between backup/batch steps cause the operation to stop. Remote backup files can be imported as `.bak` files and sent back through the manager.

PKG Relay provides local database management and setup instructions. Use its **Open database folder** option and an FTP client to copy the desired JSON files to the same console folder.

## Verification

- Live downloader verification on 2026-10-01 validated the supplied files: 866 Games entries (376,672 bytes) and 5 DLC entries (2,165 bytes). Counts reflect the provider's files at that time.
- 215 core tests pass: 114 Orbis Toolz and 101 PKG Relay. Added checks cover default/persisted sources, both downloads, invalid-second-file preservation, imports/backups/restores, cancellation cleanup and independent Homebrew generation. FTP protocol tests cover successful staged replacement, failed-rename preservation and a console-address change mid-upload.
- Native WinUI checks exercise the manager's default selections, download action, validation, status refresh and InfoBar result, alongside existing Homebrew navigation/layout/browser checks. The database responses are mocked and the tests do not contact a PS4.
- Both apps compile in Debug and Release. Orbis Debug was built to an alternate output folder because the user's existing Debug executable was running. PKG Relay retains its existing PkgPage.xaml WMC1506 warning.
- Direct FTP transfer has protocol-level coverage; physical PS4/FPKGi verification remains a console check. No resident payload update is required.

Portable releases are in each repository's artifacts folder. Keep each executable with its release files and restart using the new build.
