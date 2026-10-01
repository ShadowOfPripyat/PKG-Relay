# RemotePS4_PkgSender

Run the core and local HTTP regression checks with `./scripts/Test.ps1`, or:

```powershell
dotnet test tests/PkgRelay.Core.Tests/PkgRelay.Core.Tests.csproj -c Release
```

These tests compile the production service and model files directly. They cover
RPI parsing, metadata edge cases, interrupted queue recovery, concurrent saves,
and HTTP package serving. They do not exercise WinUI interactions or install a
package on a real PS4.

