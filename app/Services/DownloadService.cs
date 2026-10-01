namespace PkgRelay.Services;

public sealed class DownloadService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly HttpClient _http;

    public DownloadService(SettingsService settings, LogService log) : this(settings, log,
            new SocketsHttpHandler
            {
                UseProxy = false,
                AutomaticDecompression =
                    System.Net.DecompressionMethods.GZip |
                    System.Net.DecompressionMethods.Deflate |
                    System.Net.DecompressionMethods.Brotli,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            }) { }

    internal DownloadService(SettingsService settings, LogService log, HttpMessageHandler handler)
    {
        _settings = settings;
        _log = log;
        _http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<string> DownloadAsync(
        string url,
        string suggestedName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Download URL is invalid.");

        var directory = ResolveDownloadDirectory();
        Directory.CreateDirectory(directory);

        var fileName = MakeSafeFileName(suggestedName);

        if (!fileName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            fileName += ".pkg";

        var destination = GetUniquePath(
            Path.Combine(directory, fileName));

        var temporary = destination + ".part";

        await _log.InfoAsync($"Downloading Homebrew PKG: {uri}");

        try
        {
            using var response =
                await _http.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("The download returned a webpage instead of a PS4 PKG. The provider may require browser verification.");

            var total = response.Content.Headers.ContentLength ?? -1L;

            await using var source =
                await response.Content.ReadAsStreamAsync(cancellationToken);

            await using var target = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[1024 * 1024];
            long readTotal = 0;

            while (true)
            {
                var read = await source.ReadAsync(
                    buffer,
                    cancellationToken);

                if (read == 0)
                    break;

                await target.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);

                readTotal += read;

                if (total > 0)
                    progress?.Report(readTotal * 100.0 / total);
            }

            await target.FlushAsync(cancellationToken);
            // Close the file before validation/rename: FileShare.None prevents
            // renaming an open download on Windows.
            await target.DisposeAsync();
            await using (var check = File.OpenRead(temporary))
            {
                var signature = new byte[4];
                if (await check.ReadAsync(signature, cancellationToken) != 4 ||
                    !signature.AsSpan().SequenceEqual(new byte[] { 0x7F, 0x43, 0x4E, 0x54 }))
                    throw new InvalidDataException("The download did not contain a valid PS4 PKG header.");
            }

            File.Move(temporary, destination);
            progress?.Report(100);

            await _log.InfoAsync($"Downloaded Homebrew PKG: {destination}");

            return destination;
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
            }

            throw;
        }
    }

    private string ResolveDownloadDirectory()
    {
        if (!string.IsNullOrWhiteSpace(
                _settings.Current.HomebrewDownloadFolder))
        {
            return Path.GetFullPath(
                _settings.Current.HomebrewDownloadFolder);
        }

        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            "Downloads",
            "PKG Relay");
    }

    public async Task<string> AcceptBrowserDownloadAsync(string path, string suggestedName, CancellationToken token = default)
    {
        await using (var check = File.OpenRead(path))
        {
            var header = new byte[4];
            if (await check.ReadAsync(header, token) != 4 || !header.AsSpan().SequenceEqual(new byte[] { 0x7F, 0x43, 0x4E, 0x54 }))
                throw new InvalidDataException("The browser download is not a PS4 PKG. Try a direct package link.");
        }
        var directory = ResolveDownloadDirectory();
        Directory.CreateDirectory(directory);
        var destination = GetUniquePath(Path.Combine(directory, MakeSafeFileName(suggestedName) + ".pkg"));
        var temporary = destination + ".part";
        try
        {
            await using (var source = File.OpenRead(path))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                await source.CopyToAsync(target, token);
            File.Move(temporary, destination);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string MakeSafeFileName(string name)
    {
        var value =
            string.IsNullOrWhiteSpace(name)
                ? "homebrew"
                : name.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');

        return value;
    }

    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 2; ; index++)
        {
            var candidate =
                Path.Combine(
                    directory,
                    $"{name} ({index}){extension}");

            if (!File.Exists(candidate))
                return candidate;
        }
    }

    public void Dispose() => _http.Dispose();
}
