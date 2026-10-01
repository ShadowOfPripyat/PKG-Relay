using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PkgRelay.Models;
namespace PkgRelay.Services;
internal static class HomebrewCatalog
{
    internal static HomebrewItem[] Parse(JsonElement root, Uri endpoint)
    {
        var result = new List<HomebrewItem>();
        Read(root, endpoint, result, 0);
        if (result.Count == 0 && !HasCatalogShape(root))
            throw new InvalidDataException("The provider did not return a supported JSON package catalog. Use a JSON catalog URL, rather than a website homepage.");
        return result.DistinctBy(x => x.PackageUrl, StringComparer.Ordinal).ToArray();
    }
    private static bool HasCatalogShape(JsonElement root) => root.ValueKind == JsonValueKind.Array ||
        root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Any(x =>
            new[] { "data", "packages", "results", "items", "apps", "homebrew" }.Contains(x.Name, StringComparer.OrdinalIgnoreCase) &&
            x.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object);
    private static void Read(JsonElement value, Uri endpoint, List<HomebrewItem> result, int depth, string key = "")
    {
        if (depth > 8) return;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in value.EnumerateArray()) Read(entry, endpoint, result, depth + 1);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) return;
        var link = Text(value, "package_url", "pkg_url", "download_url", "package", "pkg", "pkgurl", "download", "url", "link");
        if (link.Length == 0 && endpoint.Host == "api.pkg-zone.com" && Regex.IsMatch(Text(value, "id", "title_id"), @"^[A-Z0-9]{9}$"))
            link = "https://api.pkg-zone.com/download.php?tid=" + Uri.EscapeDataString(Text(value, "id", "title_id"));
        if (link.Length == 0 && Uri.TryCreate(key, UriKind.Absolute, out _)) link = key;
        if (link.Length > 0 && Uri.TryCreate(endpoint, link, out var package) && IsHttp(package))
        {
            var size = Text(value, "size", "filesize", "file_size");
            var date = Text(value, "releaseddate", "release_date", "release", "published_at");
            var image = Text(value, "image", "icon", "icon_url", "image_url", "cover_url", "cover", "picpath");
            result.Add(new HomebrewItem
            {
                Name = Text(value, "name", "title", "app_name", "package_name") is { Length: > 0 } name ? name : "Homebrew package",
                Description = string.Join("\n", new[] { Text(value, "description", "summary", "desc"), Text(value, "desc_1"), Text(value, "desc_2") }.Where(x => x.Length > 0)),
                Author = Text(value, "author", "developer", "publisher"),
                Category = Text(value, "category", "apptype", "type", "genre") is { Length: > 0 } category ? category : "Homebrew",
                PackageUrl = package.AbsoluteUri,
                ImageUrl = Uri.TryCreate(endpoint, image, out var icon) && image.Length > 0 && IsHttp(icon) ? icon.AbsoluteUri : "",
                ContentId = Text(value, "content_id", "contentId"), TitleId = Text(value, "title_id", "titleId", "cusa", "tid", "id"),
                Version = Text(value, "version", "app_ver", "appVersion"),
                Size = long.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) ? Math.Max(0, bytes) : 0,
                ReleaseDate = ParseDate(date), Firmware = Text(value, "firmware", "min_fw", "min_firmware", "fw"),
                SourceName = Text(value, "source_name") is { Length: > 0 } source ? source : endpoint.Host,
                SourceUrl = Text(value, "source_url"), AppKey = Text(value, "app_key")
            });
            return;
        }
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) continue;
            if (new[] { "data", "packages", "results", "items", "apps", "homebrew" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase) || Uri.TryCreate(property.Name, UriKind.Absolute, out _))
                Read(property.Value, endpoint, result, depth + 1, property.Name);
        }
    }
    private static DateTimeOffset? ParseDate(string value)
    {
        // Store/FPKGi exports use day-month-year, while release APIs use ISO.
        if (DateTimeOffset.TryParseExact(value, new[] { "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ||
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)) return date;
        return null;
    }
    internal static string Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    return property.Value.ToString().Trim();
        return "";
    }
    internal static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https";
    internal static HomebrewItem[] FromRelease(JsonElement release, string name, string author, string category, string repository = "", string image = "")
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return [];
        var result = new List<HomebrewItem>();
        foreach (var asset in assets.EnumerateArray())
        {
            var filename = Text(asset, "name");
            var url = Text(asset, "browser_download_url");
            if (!filename.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) || filename.Contains("ps5", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsHttp(uri)) continue;
            var id = Regex.Match(filename, @"[A-Z]{2}\d{4}-[A-Z0-9]{9}_\d{2}-[A-Z0-9_]{1,16}").Value;
            result.Add(new HomebrewItem
            {
                Name = name, Author = author, Category = category, Description = Text(release, "name"), PackageUrl = url,
                ContentId = id, TitleId = id.Length >= 16 ? id.Substring(7, 9) : "", Version = Text(release, "tag_name").TrimStart('v', 'V'),
                Size = asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) ? Math.Max(0, bytes) : 0,
                ReleaseDate = DateTimeOffset.TryParse(Text(release, "published_at"), out var date) ? date : null,
                SourceName = "GitHub • " + (repository.Length > 0 ? repository : author), SourceUrl = Text(release, "html_url"),
                ImageUrl = image, IsOfficial = true, AppKey = repository, AssetName = filename,
                IsPrerelease = release.TryGetProperty("prerelease", out var preview) && preview.ValueKind == JsonValueKind.True,
                Firmware = FirmwareFromText(filename, Text(release, "body"))
            });
        }
        return result.ToArray();
    }
    private static string FirmwareFromText(string filename, string body)
    {
        const string version = @"[\s:_-]*(\d{1,2}\.\d{2})(?:\s*(?:to|[-–])\s*(\d{1,2}\.\d{2}))?(\+|\s+or newer)?";
        var match = Regex.Match(filename, @"(?:FW|firmware)" + version, RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(body, @"(?:minimum|required|requires|supports|supported|for)\s+(?:PS4\s+)?(?:FW|firmware)" + version, RegexOptions.IgnoreCase);
        return !match.Success ? "" : match.Groups[1].Value + (match.Groups[2].Success ? "–" + match.Groups[2].Value : "") + (match.Groups[3].Success ? "+" : "");
    }
    internal static string Identity(HomebrewItem item)
    {
        var name = Regex.Replace(item.Name.ToLowerInvariant(), @"[^a-z0-9]", "");
        if (name.Contains("apollo")) return "apollo";
        if (name.Contains("xplorer") && (name.Contains("20") || item.TitleId == "LAPY20009")) return "xplorer2";
        if (name.Contains("temperature")) return "temperature";
        if (name.Contains("cheat") && name.Contains("manager")) return "cheat-manager";
        if (name.Contains("payloadguest")) return "payload-guest";
        if (name.Contains("webdav")) return "webdav";
        if (name.Contains("ezremote")) return "ezremote";
        if (name.Contains("fpkgi")) return "fpkgi";
        if (name.Contains("wiliwili")) return "wiliwili";
        if (name.Contains("remote") && (name.Contains("pkginstaller") || name.Contains("packageinstaller"))) return "rpi";
        return item.AppKey.Length > 0 ? item.AppKey : item.TitleId.Length > 0 ? item.TitleId : name;
    }
    internal static HomebrewApp[] Group(IEnumerable<HomebrewItem> items) => items.GroupBy(Identity, StringComparer.OrdinalIgnoreCase)
        .Select(group => new HomebrewApp(group.DistinctBy(x => x.PackageUrl).OrderBy(x => x.IsPrerelease)
            .ThenByDescending(x => x.IsOfficial).ThenByDescending(x => x.ReleaseDate)
            .ThenByDescending(x => x.Version, VersionComparer.Instance).ThenBy(x => x.Firmware, StringComparer.Ordinal).ToArray())).ToArray();
    internal static HomebrewApp[] QueryApps(IEnumerable<HomebrewApp> apps, string search, string category, int sort)
    {
        var filtered = apps.Where(x => Query(x.Versions, search, category, 0).Length > 0);
        var names = StringComparer.CurrentCultureIgnoreCase;
        return (sort switch
        {
            1 => filtered.OrderByDescending(x => x.Name, names),
            2 => filtered.OrderByDescending(x => x.Versions[0].ReleaseDate).ThenBy(x => x.Name, names),
            3 => filtered.OrderBy(x => x.SelectedVersion.Size <= 0 ? long.MaxValue : x.SelectedVersion.Size).ThenBy(x => x.Name, names),
            4 => filtered.OrderByDescending(x => x.SelectedVersion.Size).ThenBy(x => x.Name, names),
            _ => filtered.OrderBy(x => x.Name, names)
        }).ToArray();
    }
    internal static string FpkgiDatabase(IEnumerable<HomebrewItem> selectedVersions)
    {
        var entries = selectedVersions.DistinctBy(x => x.PackageUrl).ToDictionary(x => x.PackageUrl,
            x => new { name = x.Name, version = x.Version, release = x.ReleaseDate?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture), size = x.Size,
                min_fw = Regex.Match(x.Firmware, @"^\d{1,2}\.\d{2}") is { Success: true } fw ? fw.Value : null, cover_url = x.ImageUrl, region = (string?)null });
        if (entries.Count == 0) throw new InvalidOperationException("Load a Homebrew catalog before exporting its FPKGi database.");
        return JsonSerializer.Serialize(new { DATA = entries }, new JsonSerializerOptions { WriteIndented = true });
    }
    private sealed class VersionComparer : IComparer<string>
    {
        internal static readonly VersionComparer Instance = new();
        public int Compare(string? left, string? right)
        {
            var a = Regex.Matches(left ?? "", @"\d+").Select(x => long.TryParse(x.Value, out var n) ? n : 0).ToArray();
            var b = Regex.Matches(right ?? "", @"\d+").Select(x => long.TryParse(x.Value, out var n) ? n : 0).ToArray();
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var comparison = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }
    internal static HomebrewItem[] Query(IEnumerable<HomebrewItem> catalog, string search, string category, int sort)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = catalog.Where(x => (category.Length == 0 || string.Equals(x.Category, category, StringComparison.CurrentCultureIgnoreCase)) && words.All(word => string.Join(' ', x.Name, x.Description, x.Author, x.Category, x.TitleId, x.ContentId, x.Version, x.Firmware, x.SourceName).Contains(word, StringComparison.CurrentCultureIgnoreCase)));
        var byName = StringComparer.CurrentCultureIgnoreCase;
        return (sort switch
        {
            1 => filtered.OrderByDescending(x => x.Name, byName),
            2 => filtered.OrderByDescending(x => x.ReleaseDate).ThenBy(x => x.Name, byName),
            3 => filtered.OrderBy(x => x.Size <= 0 ? long.MaxValue : x.Size).ThenBy(x => x.Name, byName),
            4 => filtered.OrderByDescending(x => x.Size).ThenBy(x => x.Name, byName),
            _ => filtered.OrderBy(x => x.Name, byName)
        }).ToArray();
    }
}
