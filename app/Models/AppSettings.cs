namespace PkgRelay.Models;

public sealed class AppSettings
{
    public string Ps4Address { get; set; } = "";
    public int RpiPort { get; set; } = 12800;

    // Empty means route-based automatic selection.
    public string LocalAddress { get; set; } = "";
    public int HttpPort { get; set; } = 8080;

    public int RequestTimeoutMs { get; set; } = 5000;
    public int PollIntervalMs { get; set; } = 2500;
    public int MaxProgressFailures { get; set; } = 8;

    public bool RecursiveScan { get; set; } = true;
    public bool AutoStartPackages { get; set; } = false;
    public bool AutoDiscoverPs4OnConnectionFailure { get; set; } = false;
    public bool ShowHomebrewTab { get; set; } = false;
    public string PackageFolder { get; set; } = "";
    public bool ShowPkgListBorders { get; set; } = false;
    public bool WatchDefaultPkgFolder { get; set; } = false;

    // The provider accepts a full JSON catalog endpoint.
    public string HomebrewApiUrl { get; set; } = "http://api.pkg-zone.com/";

    // Empty uses Downloads\PKG Relay.
    public string HomebrewDownloadFolder { get; set; } = "";
}
