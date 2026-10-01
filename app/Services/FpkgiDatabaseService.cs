using System.Net;
using System.Text.Json;
using PkgRelay.Models;

namespace PkgRelay.Services;

public enum FpkgiDatabaseKind { Games, Dlc, Homebrew }
public sealed record FpkgiDatabaseInfo(FpkgiDatabaseKind Kind, string Path, int Entries, long Bytes, DateTime Updated);
public sealed record FpkgiDatabaseProgress(string Message, double Percent);

public sealed class FpkgiDatabaseService : IDisposable
{
    public const string DefaultGamesUrl = "https://archive.org/download/ps4-fpkg-collection-english-fpkgi/GAMES.json";
    public const string DefaultDlcUrl = "https://archive.org/download/ps4-fpkg-collection-english-fpkgi/DLC.json";
    private const long MaximumBytes = 64 * 1024 * 1024;
    private readonly SettingsService settings;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Folder => Path.Combine(settings.DataDirectory, "FPKGi");
    public FpkgiDatabaseService(SettingsService settings) : this(settings, new SocketsHttpHandler
    { UseProxy = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) }) { }
    internal FpkgiDatabaseService(SettingsService settings, HttpMessageHandler handler)
    {
        this.settings = settings;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OrbisToolz-PKGRelay-FPKGi/1.0");
    }
    public static string FileName(FpkgiDatabaseKind kind) => kind switch
    { FpkgiDatabaseKind.Games => "GAMES.json", FpkgiDatabaseKind.Dlc => "DLC.json", FpkgiDatabaseKind.Homebrew => "HOMEBREW.json", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    public string PathFor(FpkgiDatabaseKind kind) => Path.Combine(Folder, FileName(kind));
    public static string RemotePath(FpkgiDatabaseKind kind) => "/user/data/FPKGi/ContentJSONs/" + FileName(kind);
    public static Uri SourceUri(string source)
    {
        if (!Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) || !HomebrewCatalog.IsHttp(uri))
            throw new ArgumentException("Database sources must be HTTP or HTTPS URLs.");
        return uri;
    }
    public async Task<FpkgiDatabaseInfo> InspectAsync(FpkgiDatabaseKind kind, CancellationToken token = default)
    {
        var path = PathFor(kind);
        var count = await ValidateAsync(path, token);
        var file = new FileInfo(path);
        return new(kind, path, count, file.Length, file.LastWriteTime);
    }
    internal static async Task<int> ValidateAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("FPKGi databases must be smaller than 64 MB.");
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("DATA", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A FPKGi database needs a DATA object containing package URLs and metadata.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in data.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(entry.Name, UriKind.Absolute, out var url) || !HomebrewCatalog.IsHttp(url) || !keys.Add(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object ||
                !entry.Value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                throw new InvalidDataException("Each database entry needs a unique HTTP/HTTPS package URL and an app name.");
        }
        if (keys.Count == 0) throw new InvalidDataException("The database contains no packages.");
        return keys.Count;
    }
    public async Task DownloadAsync(IEnumerable<FpkgiDatabaseKind> selected, IProgress<FpkgiDatabaseProgress>? progress = null, CancellationToken token = default)
    {
        var kinds = selected.Distinct().ToArray();
        if (kinds.Length == 0) throw new InvalidOperationException("Select Games or DLC to download.");
        var sources = kinds.Select(kind => (Kind: kind, Uri: SourceUri(kind switch
        { FpkgiDatabaseKind.Games => settings.Current.FpkgiGamesDatabaseUrl, FpkgiDatabaseKind.Dlc => settings.Current.FpkgiDlcDatabaseUrl, _ => throw new InvalidOperationException("Generate the Homebrew database from the catalog instead.") }))).ToArray();
        await gate.WaitAsync(token);
        var staged = new List<(FpkgiDatabaseKind Kind, string Path)>();
        try
        {
            Directory.CreateDirectory(Folder);
            for (var i = 0; i < sources.Length; i++)
            {
                var (kind, uri) = sources[i];
                var temporary = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".part");
                staged.Add((kind, temporary));
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidDataException("The database source returned a webpage. Check the source URL or import a JSON file downloaded in your browser.");
                var total = response.Content.Headers.ContentLength;
                if (total > MaximumBytes) throw new InvalidDataException("The database exceeds 64 MB.");
                await using (var source = await response.Content.ReadAsStreamAsync(token))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536]; long downloaded = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        downloaded += read;
                        if (downloaded > MaximumBytes) throw new InvalidDataException("The database exceeds 64 MB.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        progress?.Report(new("Downloading " + FileName(kind), (i + (total > 0 ? Math.Min(1, downloaded / (double)total.Value) : 0)) * 100 / kinds.Length));
                    }
                }
                await ValidateAsync(temporary, token);
            }
            // Validate the entire selection before replacing any working files.
            token.ThrowIfCancellationRequested();
            foreach (var entry in staged) Backup(entry.Kind);
            var committed = new List<FpkgiDatabaseKind>();
            try
            {
                foreach (var entry in staged) { File.Move(entry.Path, PathFor(entry.Kind), true); committed.Add(entry.Kind); }
            }
            catch
            {
                foreach (var kind in committed)
                {
                    var backup = PathFor(kind) + ".bak";
                    if (File.Exists(backup)) File.Copy(backup, PathFor(kind), true); else File.Delete(PathFor(kind));
                }
                throw;
            }
            progress?.Report(new("Databases downloaded and validated", 100));
        }
        finally { foreach (var entry in staged) { try { File.Delete(entry.Path); } catch (IOException) { } } gate.Release(); }
    }
    public async Task ImportAsync(FpkgiDatabaseKind kind, string source, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Folder);
            temporary = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".part");
            if (new FileInfo(source).Length > MaximumBytes) throw new InvalidDataException("The database exceeds 64 MB.");
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, token);
            await ValidateAsync(temporary, token);
            token.ThrowIfCancellationRequested();
            Backup(kind); File.Move(temporary, PathFor(kind), true);
        }
        finally { if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } } gate.Release(); }
    }
    public async Task GenerateHomebrewAsync(IEnumerable<HomebrewItem> packages, CancellationToken token = default)
    {
        var json = HomebrewCatalog.FpkgiDatabase(packages);
        Directory.CreateDirectory(Folder);
        var temporary = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".json");
        try { await File.WriteAllTextAsync(temporary, json, token); await ImportAsync(FpkgiDatabaseKind.Homebrew, temporary, token); }
        finally { File.Delete(temporary); }
    }
    public async Task RestoreAsync(FpkgiDatabaseKind kind, CancellationToken token = default)
    {
        var backup = PathFor(kind) + ".bak";
        if (!File.Exists(backup)) throw new FileNotFoundException("No previous version is saved for " + FileName(kind));
        await ImportAsync(kind, backup, token);
    }
    private void Backup(FpkgiDatabaseKind kind)
    {
        var path = PathFor(kind);
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        else if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
    }
    public void Dispose() => http.Dispose();
}
