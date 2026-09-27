namespace PkgRelay.Services;

public sealed class DownloadService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly HttpClient _http;

    public DownloadService(SettingsService settings, LogService log)
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
                ConnectTimeout = TimeSpan.FromSeconds(10)
            })
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
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
