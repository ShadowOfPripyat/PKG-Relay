# Homebrew crash fix — Orbis Toolz 0.9.15 / PKG Relay 0.5.3

The native UI smoke check reproduced the Homebrew crash with packages that have no artwork. The generated `x:Bind` called `XamlBindingHelper.ConvertValue(ImageSource, null)`, which threw `ArgumentException` (`0x80070057`) during data-template rendering. The catalog/network tests could not detect this because they do not instantiate WinUI controls.

All three Homebrew templates in both apps now use `HomebrewImageConverter`, which returns a typed `BitmapImage` for HTTP/HTTPS artwork and null for missing, blank, malformed or unsafe URLs. A null image source leaves the fallback icon visible. This avoids the generated string conversion. Image download failures continue to use the existing fallback behavior.

Orbis Toolz also initializes its application XAML resources explicitly and writes unhandled WinUI exception details to `runtime-crash.log` in its application data directory. The logger does not suppress unhandled exceptions.

The regression harness under `scripts/HomebrewUiSmoke` links the actual Homebrew page XAML, code-behind, image converter and InfoBar behavior. It uses a synthetic HTTP catalog and isolated settings/cache under its own output folder. It never downloads or queues packages or contacts a PS4. Its native test window stays off screen without activation, so templates and real control events can load. It checks missing/invalid/valid artwork, catalog rendering, debounced search, category/sort controls, list/grid/tile changes, page unload/reload, and InfoBar state/closing. This is a control-level smoke check; it does not visually inspect animation appearance.

Run on Windows with the matching Windows App Runtime installed:

```powershell
pwsh -File scripts/Test-HomebrewUi.ps1
```

Debug and Release builds succeed in both apps. The existing suites still pass all 186 tests (98 Orbis Toolz, 88 PKG Relay).

Portable application folders: `artifacts/OrbisToolz-0.9.15-win-x64` and `artifacts/PKG-Relay-0.5.3-win-x64` in their respective repositories. Keep each executable with the other files in its folder. No PS4 payload update is needed.
