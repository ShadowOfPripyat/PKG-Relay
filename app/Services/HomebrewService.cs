using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PkgRelay.Models;
namespace PkgRelay.Services;
public sealed class HomebrewService : IDisposable
{
    private readonly SettingsService settings;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    public string ProviderName { get; private set; } = "Official project releases";
    public string Notice { get; private set; } = "";
    private static readonly (string Repository, string Name, string Author, string Category, string Image)[] Sources =
    [
        ("bucanero/apollo-ps4", "Apollo Save Tool", "bucanero", "Utilities", "https://raw.githubusercontent.com/bucanero/apollo-ps4/main/sce_sys/icon0.png"),
        ("GoldHEN/GoldHEN_Cheat_Manager", "GoldHEN Cheat Manager", "GoldHEN", "Utilities", "https://raw.githubusercontent.com/GoldHEN/GoldHEN_Cheat_Manager/main/sce_sys/icon0.png"),
        ("cy33hc/ps4-webdav-client", "PS4 WebDAV Client", "cy33hc", "File managers", "https://raw.githubusercontent.com/cy33hc/ps4-webdav-client/master/data/sce_sys/icon0.png"),
        ("cy33hc/ps4-ezremote-client", "PS4 EZ Remote Client", "cy33hc", "File managers", "https://raw.githubusercontent.com/cy33hc/ps4-ezremote-client/master/data/sce_sys/icon0.png"),
        ("cy33hc/ps4-ftp-client", "PS4 FTP Client", "cy33hc", "File managers", "https://raw.githubusercontent.com/cy33hc/ps4-ftp-client/master/data/sce_sys/icon0.png"),
        ("LightningMods/Itemzflow", "Itemzflow", "LightningMods", "Utilities", ""),
        ("Al-Azif/ps4-payload-guest", "Payload Guest", "Al-Azif", "Utilities", "https://raw.githubusercontent.com/Al-Azif/ps4-payload-guest/main/sce_sys/icon0.png"),
        ("ItsJokerZz/FPKGi", "FPKGi", "ItsJokerZz", "Package installers", "https://raw.githubusercontent.com/ItsJokerZz/FPKGi/release/source/Assets/Images/App/Icon.png"),
        ("xfangfang/wiliwili", "Wiliwili", "xfangfang", "Media", "https://raw.githubusercontent.com/xfangfang/wiliwili/yoga/scripts/ps4/sce_sys/icon0.png")
    ];
    public HomebrewService(SettingsService settings) : this(settings, new SocketsHttpHandler
    {
        UseProxy = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(8)
    }) { }
    internal HomebrewService(SettingsService settings, HttpMessageHandler handler)
    {
        this.settings = settings;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OrbisToolz-PKGRelay-Homebrew/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }
    public Task<IReadOnlyList<HomebrewItem>> GetCatalogAsync(CancellationToken token = default) => LoadAsync(token);
    public async Task<IReadOnlyList<HomebrewItem>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Notice = "";
            var configured = settings.Current.HomebrewApiUrl.Trim();
            var legacy = Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) && configuredUri.Host is "api.pkg-zone.com" or "pkg-zone.com" && configuredUri.AbsolutePath == "/";
            var builtIn = configured.Length == 0 || legacy;
            if (!builtIn && (configuredUri is null || !HomebrewCatalog.IsHttp(configuredUri))) throw new InvalidOperationException("The Homebrew catalog URL must use HTTP or HTTPS.");
            var key = builtIn ? "official-releases-v1" : configuredUri!.AbsoluteUri;
            ProviderName = builtIn ? "Official project releases" : configuredUri!.Host;
            var cache = Path.Combine(settings.DataDirectory, "Homebrew", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".json");
            HomebrewItem[] items;
            try
            {
                if (builtIn)
                {
                    var releases = await Task.WhenAll(Sources.Select(async source =>
                    {
                        try
                        {
                            using var json = await GetJsonAsync(new Uri($"https://api.github.com/repos/{source.Repository}/releases?per_page=100"), cancellationToken);
                            var entries = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : new[] { json.RootElement };
                            return entries.Where(x => !x.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.True)
                                .SelectMany(x => HomebrewCatalog.FromRelease(x, source.Name, source.Author, source.Category, source.Repository, source.Image)).ToArray();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return Array.Empty<HomebrewItem>(); }
                    }));
                    items = releases.SelectMany(x => x).Concat(KnownStoreApps()).ToArray();
                    if (releases.All(x => x.Length == 0)) throw new IOException("Official release feeds are unavailable or the GitHub API rate limit was reached. Try refreshing later.");
                    if (legacy) Notice = "Connect the full PKG-Zone catalog to complete the website's browser verification. Official project downloads are available now.";
                    if (releases.Count(x => x.Length > 0) < Sources.Length) Notice = (Notice + " Some projects did not return an available PS4 package.").Trim();
                }
                else
                {
                    using var json = await GetJsonAsync(configuredUri!, cancellationToken);
                    items = HomebrewCatalog.Parse(json.RootElement, configuredUri!);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<HomebrewItem[]>(await File.ReadAllTextAsync(cache, cancellationToken));
                    if (saved is null || saved.Length == 0) throw new InvalidDataException();
                    Notice = $"Showing the last saved catalog. Refresh failed: {ex.Message}";
                    return await MergeImportedAsync(saved, builtIn, cancellationToken);
                }
                catch (Exception cacheError) when (cacheError is IOException or JsonException or UnauthorizedAccessException)
                {
                    var imported = await MergeImportedAsync([], builtIn, cancellationToken);
                    if (imported.Length == 0) throw new IOException(ex.Message, ex);
                    Notice += " Official releases could not refresh: " + ex.Message;
                    return imported;
                }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                await File.WriteAllTextAsync(cache + ".tmp", JsonSerializer.Serialize(items), cancellationToken);
                File.Move(cache + ".tmp", cache, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return await MergeImportedAsync(items, builtIn, cancellationToken);
        }
        finally { gate.Release(); }
    }
    private async Task<HomebrewItem[]> MergeImportedAsync(HomebrewItem[] items, bool builtIn, CancellationToken token)
    {
            if (builtIn)
            {
                try
                {
                    var imported = JsonSerializer.Deserialize<HomebrewItem[]>(await File.ReadAllTextAsync(ImportedCatalogPath, token));
                    if (imported is { Length: > 0 })
                    {
                        items = items.Concat(imported).DistinctBy(x => x.PackageUrl).ToArray();
                        ProviderName = "PKG-Zone + official project releases";
                        Notice = (Notice + " Full catalog saved on this PC. Use Connect full catalog to update the store snapshot; GitHub versions refresh automatically.").Trim();
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return items;
    }
    private string ImportedCatalogPath => Path.Combine(settings.DataDirectory, "Homebrew", "store-catalog.json");
    public async Task<int> ImportCatalogAsync(string path, CancellationToken token = default)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Catalog files must be smaller than 64 MB.");
        HomebrewItem[] items;
        if (Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase))
            items = await Task.Run(() => HomebrewStoreDatabase.Read(path), token);
        else
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            items = HomebrewCatalog.Parse(json.RootElement, new Uri("https://api.pkg-zone.com/"));
        }
        if (items.Length == 0) throw new InvalidDataException("The file has no usable package links.");
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ImportedCatalogPath)!);
            await File.WriteAllTextAsync(ImportedCatalogPath + ".tmp", JsonSerializer.Serialize(items), token);
            File.Move(ImportedCatalogPath + ".tmp", ImportedCatalogPath, true);
        }
        finally { gate.Release(); }
        return items.Length;
    }
    private static HomebrewItem[] KnownStoreApps() =>
    [
        new() { Name = "PS4 Xplorer 2.0", Author = "Lapy", Category = "File managers", TitleId = "LAPY20009", Version = "2.06", Firmware = "5.05–12.52 (developer listed)", PackageUrl = "https://pkg-zone.com/download/ps4/LAPY20009/latest", SourceUrl = "https://lapygames.wordpress.com/apps/", SourceName = "PKG-Zone • Lapy", ImageUrl = "https://lapygames.wordpress.com/wp-content/uploads/2022/10/icono.png" },
        new() { Name = "PS4 Temperature", Author = "Lapy", Category = "Utilities", TitleId = "LAPY20006", Version = "1.07", Firmware = "5.05–11.00 (developer listed)", PackageUrl = "https://pkg-zone.com/download/ps4/LAPY20006/latest", SourceUrl = "https://lapygames.wordpress.com/apps/", SourceName = "PKG-Zone • Lapy", ImageUrl = "https://lapygames.wordpress.com/wp-content/uploads/2020/10/icono.png" },
        new() { Name = "Remote Package Installer", Author = "flatz", Category = "Package installers", TitleId = "FLTZ00003", PackageUrl = "https://pkg-zone.com/download/ps4/FLTZ00003/latest", SourceUrl = "https://github.com/flatz/ps4_remote_pkg_installer", SourceName = "PKG-Zone • flatz", ImageUrl = "https://raw.githubusercontent.com/flatz/ps4_remote_pkg_installer/master/sce_sys/icon0.png" }
    ];
    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken token)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(response.StatusCode == HttpStatusCode.Forbidden
            ? "The provider returned HTTP 403. It may require browser verification or have reached its API rate limit. Use a public JSON catalog URL or leave it empty for official project releases."
            : $"The catalog provider returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true) throw new InvalidDataException("The catalog URL returned a webpage. Use a JSON catalog URL or leave it empty for official releases.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        try { return await JsonDocument.ParseAsync(stream, cancellationToken: token); }
        catch (JsonException ex) { throw new InvalidDataException("The provider response was not a valid JSON catalog.", ex); }
    }
    public void Dispose() => http.Dispose();
}
