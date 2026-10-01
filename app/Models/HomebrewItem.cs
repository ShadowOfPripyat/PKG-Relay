namespace PkgRelay.Models;
public sealed class HomebrewItem
{
    public string Name { get; set; } = "";
    public string Title { get => Name; set => Name = value; }
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Category { get; set; } = "Homebrew";
    public string PackageUrl { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DisplayImageUrl => string.IsNullOrWhiteSpace(ImageUrl) ? null : ImageUrl;
    public string ContentId { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string Version { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset? ReleaseDate { get; set; }
    public string Firmware { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string AppKey { get; set; } = "";
    public bool IsOfficial { get; set; }
    public bool IsPrerelease { get; set; }
    public string FirmwareText => string.IsNullOrWhiteSpace(Firmware) ? "Firmware not specified" : $"Firmware: {Firmware}";
    public string DateText => ReleaseDate is { } date ? $"Released {date.LocalDateTime:d}" : "Release date not supplied";
    public string VersionLabel => $"{(Version.Length == 0 ? "Unspecified version" : Version)}{(IsPrerelease ? " (prerelease)" : "")} • {(Firmware.Length == 0 ? "FW unspecified" : Firmware)} • {SourceName}";
    public string SecondaryText => string.Join(" • ", new[] { Version, Author, Category, TitleId }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string Subtitle => SecondaryText;
    public string SizeText => Size <= 0 ? "Size unknown" : Size >= 1073741824 ? $"{Size / 1073741824d:0.##} GB" : $"{Size / 1048576d:0.##} MB";
}
