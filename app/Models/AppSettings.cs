namespace PkgRelay.Models;

public sealed class AppSettings
{
    public string Ps4Address { get; set; } = "";
    public int RpiPort { get; set; } = 12800;
    public int ResidentRpiPort { get; set; } = 12801;
    public string ResidentRpiToken { get; set; } = "";

    // Empty means route-based automatic selection.
    public string LocalAddress { get; set; } = "";
    public int HttpPort { get; set; } = 8080;

    public int RequestTimeoutMs { get; set; } = 5000;
    public int PollIntervalMs { get; set; } = 2500;
    public ProgressUpdateRate ProgressUpdateRate { get; set; } = ProgressUpdateRate.Default;
    public int ConsoleQueueLimit { get; set; } = 4;
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectivePollIntervalMs => ProgressUpdateRate switch
    {
        ProgressUpdateRate.Fast => 500,
        ProgressUpdateRate.Realtime => 250,
        _ => Math.Clamp(PollIntervalMs, 500, 30000)
    };
    public int MaxProgressFailures { get; set; } = 8;

    public bool RecursiveScan { get; set; } = true;
    public bool AutoStartPackages { get; set; } = false;
    public bool AutoDiscoverPs4OnConnectionFailure { get; set; } = false;
    public bool ShowHomebrewTab { get; set; } = false;
    public string PackageFolder { get; set; } = "";
    public bool ShowPkgListBorders { get; set; } = false;
    public bool WatchDefaultPkgFolder { get; set; } = false;

    // The provider accepts a full JSON catalog endpoint.
    public string HomebrewApiUrl { get; set; } = "";
    public string FpkgiGamesDatabaseUrl { get; set; } = "https://archive.org/download/ps4-fpkg-collection-english-fpkgi/GAMES.json";
    public string FpkgiDlcDatabaseUrl { get; set; } = "https://archive.org/download/ps4-fpkg-collection-english-fpkgi/DLC.json";
    public int HomebrewViewMode { get; set; }
    public int HomebrewSortMode { get; set; }
    public string HomebrewCategoryFilter { get; set; } = "";

    // Empty uses Downloads\PKG Relay.
    public string HomebrewDownloadFolder { get; set; } = "";
}
