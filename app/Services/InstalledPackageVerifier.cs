using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PkgRelay.Models;

namespace PkgRelay.Services;

public enum InstalledPackageState { Unknown, NotInstalled, Installing, Installed }

public sealed class InstalledPackageVerifier : IDisposable
{
    private readonly SettingsService _settings;
    private readonly HttpClient _http;

    public InstalledPackageVerifier(SettingsService settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2)
        });
    }

    public async Task<InstalledPackageState> CheckAsync(InstallJob job, CancellationToken token = default)
    {
        // Title existence cannot prove that this particular DLC/update is installed.
        if (job.SourceKind != InstallSourceKind.LocalFile || string.IsNullOrWhiteSpace(job.ContentId) ||
            string.IsNullOrWhiteSpace(_settings.Current.Ps4Address))
            return InstalledPackageState.Unknown;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var settings = _settings.Current;
            var pairingToken = settings.ResidentRpiToken;
            var residentPort = settings.ResidentRpiPort;
            if (string.IsNullOrWhiteSpace(pairingToken))
            {
                // Reuse this Windows user's Orbis pairing only for the same console.
                var sharedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OrbisToolz", "settings.json");
                if (File.Exists(sharedPath) && new FileInfo(sharedPath).Length <= 128 * 1024)
                {
                    using var shared = JsonDocument.Parse(await File.ReadAllTextAsync(sharedPath, deadline.Token));
                    var values = shared.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
                    if (values.TryGetValue("Ps4Address", out var address) && address.GetString() == settings.Ps4Address &&
                        values.TryGetValue("ResidentRpiToken", out var paired) && paired.ValueKind == JsonValueKind.String)
                    {
                        pairingToken = paired.GetString() ?? "";
                        if (values.TryGetValue("ResidentRpiPort", out var port) && port.TryGetInt32(out var number) && number is > 0 and <= 65535)
                            residentPort = number;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(pairingToken)) return InstalledPackageState.Unknown;
            var header = new byte[4096];
            await using (var source = new FileStream(job.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             4096, FileOptions.Asynchronous))
                await source.ReadExactlyAsync(header, deadline.Token);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0x7f434e54)
                return InstalledPackageState.Unknown;
            var contentId = Encoding.ASCII.GetString(header, 0x40, 0x30).TrimEnd('\0');
            if (!string.Equals(contentId, job.ContentId, StringComparison.Ordinal) ||
                header.AsSpan(0xfe0, 32).IndexOfAnyExcept((byte)0) < 0)
                return InstalledPackageState.Unknown;
            var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x74, 4));
            var flags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x78, 4));
            var packageType = type switch { 0x1a => "PS4GD", 0x1b => "PS4AC", 0x1c => "PS4AL", _ => "" };
            if (packageType.Length == 0) return InstalledPackageState.Unknown;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri($"http://{settings.Ps4Address}:{residentPort}/api/get_install_status"))
            {
                Content = JsonContent.Create(new
                {
                    content_id = contentId,
                    package_digest = Convert.ToHexString(header.AsSpan(0xfe0, 32)).ToLowerInvariant(),
                    package_type = packageType,
                    is_patch = job.Kind == PkgKind.Update || (flags & 0x40100000u) != 0
                })
            };
            request.Headers.TryAddWithoutValidation("X-Orbis-Token", pairingToken);
            using var response = await _http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode) return InstalledPackageState.Unknown;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var root = json.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "success" ||
                !root.TryGetProperty("content_id", out var identity) || identity.GetString() != contentId ||
                !root.TryGetProperty("installed", out var installed) || installed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("installing", out var installing) || installing.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return InstalledPackageState.Unknown;
            if (installing.GetBoolean()) return InstalledPackageState.Installing;
            return installed.GetBoolean() ? InstalledPackageState.Installed : InstalledPackageState.NotInstalled;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
                                  OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            // Unavailable/old service, missing file, or unverifiable identity stays unknown.
            return InstalledPackageState.Unknown;
        }
    }

    public void Dispose() => _http.Dispose();
}
