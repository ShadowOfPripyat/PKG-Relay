using System.Text.Json;
using PkgRelay.Models;

namespace PkgRelay.Services;

public sealed class HomebrewService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly HttpClient _http;

    public HomebrewService(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;

        _http = new HttpClient(
            new SocketsHttpHandler
            {
                AutomaticDecompression =
                    System.Net.DecompressionMethods.GZip |
                    System.Net.DecompressionMethods.Deflate |
                    System.Net.DecompressionMethods.Brotli,
                ConnectTimeout = TimeSpan.FromSeconds(8)
            })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PKG-Relay-WinUI/0.5.0");
    }

    public async Task<IReadOnlyList<HomebrewItem>> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var endpoint = _settings.Current.HomebrewApiUrl.Trim();

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                "The Homebrew API URL in Settings is invalid.");

        await _log.InfoAsync($"Loading Homebrew catalog: {uri}");

        using var response =
            await _http.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);

        using var document =
            await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

        var array = FindArray(document.RootElement);

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "The configured Homebrew endpoint did not return a supported JSON catalog.");
        }

        var result = new List<HomebrewItem>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            var packageUrl = GetString(
                element,
                "package_url",
                "pkg_url",
                "download_url",
                "download",
                "url",
                "link");

            if (string.IsNullOrWhiteSpace(packageUrl))
                continue;

            var title = GetString(
                element,
                "title",
                "name",
                "app_name",
                "package_name");

            result.Add(new HomebrewItem
            {
                Title =
                    string.IsNullOrWhiteSpace(title)
                        ? "Untitled package"
                        : title,
                Description = GetString(
                    element,
                    "description",
                    "desc",
                    "summary"),
                Author = GetString(
                    element,
                    "author",
                    "developer",
                    "publisher"),
                Version = GetString(
                    element,
                    "version",
                    "app_ver",
                    "appVersion"),
                Category = GetString(
                    element,
                    "category",
                    "type",
                    "genre"),
                TitleId = GetString(
                    element,
                    "title_id",
                    "titleId",
                    "tid"),
                PackageUrl = packageUrl,
                ImageUrl = GetString(
                    element,
                    "image",
                    "icon",
                    "icon_url",
                    "image_url",
                    "cover"),
                Size = GetLong(
                    element,
                    "size",
                    "filesize",
                    "file_size")
            });
        }

        return result
            .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static JsonElement FindArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root;

        if (root.ValueKind != JsonValueKind.Object)
            return default;

        foreach (var propertyName in new[]
        {
            "packages", "items", "data", "results", "apps", "homebrew"
        })
        {
            if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Array)
                return value;

            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindArray(value);
                if (nested.ValueKind == JsonValueKind.Array)
                    return nested;
            }
        }

        // Last-resort shallow scan for the first array-valued property.
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
                return property.Value;
        }

        return default;
    }

    private static string GetString(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";

            if (value.ValueKind is JsonValueKind.Number
                or JsonValueKind.True
                or JsonValueKind.False)
                return value.ToString();
        }

        return "";
    }

    private static long GetLong(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var numeric))
                return numeric;

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), out numeric))
                return numeric;
        }

        return 0;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public void Dispose() => _http.Dispose();
}
