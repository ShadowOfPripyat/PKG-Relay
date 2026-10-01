using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PkgRelay.Models;

namespace PkgRelay.Services;

public sealed class Ps4RpiClient : IDisposable
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly HttpClient _http;
    private readonly ProgressRequestLimiter _progressLimiter = new();

    public Ps4RpiClient(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;

        _http = new HttpClient(
            new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(4),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
    }

    public async Task<bool> TestConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;

        if (string.IsNullOrWhiteSpace(settings.Ps4Address))
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);

        timeout.CancelAfter(settings.RequestTimeoutMs);

        try
        {
            using var tcp = new TcpClient();

            await tcp.ConnectAsync(
                settings.Ps4Address,
                settings.RpiPort,
                timeout.Token);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> InstallDirectAsync(
        IReadOnlyCollection<Uri> packages,
        CancellationToken cancellationToken = default,
        string contentId = "",
        int taskSubType = 6)
    {
        if (packages.Count == 0)
            throw new ArgumentException("At least one package URL is required.", nameof(packages));

        using var document = await RpiInstallRecovery.PostInstallAsync(
            PostAsync,
            new
            {
                type = "direct",
                packages = packages.Select(x => x.AbsoluteUri).ToArray()
            },
            contentId,
            taskSubType,
            _log.WarningAsync,
            cancellationToken);

        if (!document.RootElement.TryGetProperty("task_id", out var taskId) ||
            !taskId.TryGetInt32(out var id) || id < 0)
            throw new RpiException("RPI did not return a valid task_id. The package may already be installed.");

        return id;
    }

    public async Task<RpiProgress> GetProgressAsync(
        int taskId,
        CancellationToken cancellationToken = default)
    {
        using var document = await _progressLimiter.ExecuteAsync(
            () => _settings.Current.EffectivePollIntervalMs,
            () => PostAsync("get_task_progress", new { task_id = taskId }, cancellationToken),
            cancellationToken);

        return RpiProgress.FromJson(document.RootElement);
    }

    public async Task<int> FindTaskAsync(
        string contentId,
        int subType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contentId))
            throw new ArgumentException(
                "Content ID is required to find an RPI task.",
                nameof(contentId));

        using var document = await PostAsync(
            "find_task",
            new
            {
                content_id = contentId,
                sub_type = subType
            },
            cancellationToken);

        if (!document.RootElement.TryGetProperty(
                "task_id",
                out var taskId) ||
            !taskId.TryGetInt32(out var id) ||
            id < 0)
        {
            throw new RpiException(
                "RPI did not return a valid task_id for the package.");
        }

        return id;
    }

    public Task PauseAsync(int taskId, CancellationToken cancellationToken = default)
        => CommandAsync("pause_task", taskId, cancellationToken);

    public Task ResumeAsync(int taskId, CancellationToken cancellationToken = default)
        => CommandAsync("resume_task", taskId, cancellationToken);

    public Task CancelAsync(int taskId, CancellationToken cancellationToken = default)
        => CommandAsync("unregister_task", taskId, cancellationToken);

    private async Task CommandAsync(
        string route,
        int taskId,
        CancellationToken cancellationToken)
    {
        using var _ = await PostAsync(
            route,
            new { task_id = taskId },
            cancellationToken);
    }

    private async Task<JsonDocument> PostAsync(
        string route,
        object payload,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        if (string.IsNullOrWhiteSpace(settings.Ps4Address))
            throw new RpiException("PS4 address is not configured.");

        var uri = new Uri(
            $"http://{settings.Ps4Address}:{settings.RpiPort}/api/{route}");

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(settings.RequestTimeoutMs);

        var body = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        await _log.InfoAsync($"RPI POST {route}");

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        var responseText =
            await response.Content.ReadAsStringAsync(timeout.Token);

        await _log.InfoAsync(
            $"RPI {route} -> HTTP {(int)response.StatusCode}: {responseText}");

        if (!response.IsSuccessStatusCode)
        {
            throw new RpiException(
                $"RPI returned HTTP {(int)response.StatusCode}: {responseText}",
                RpiInstallRecovery.ReadError(responseText));
        }

        try
        {
            var document = RpiJson.Parse(responseText);
            if (RpiInstallRecovery.IsErrorResponse(document.RootElement))
            {
                var error = RpiInstallRecovery.ReadError(document.RootElement);
                document.Dispose();
                throw new RpiException($"RPI rejected /api/{route}: {responseText}", error);
            }
            return document;
        }
        catch (JsonException ex)
        {
            throw new RpiException(
                $"Unable to parse RPI response: {responseText}",
                ex);
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class RpiException : Exception
{
    public int? ErrorResult { get; }
    public RpiException(string message) : base(message) { }

    public RpiException(string message, int? errorResult) : base(message) => ErrorResult = errorResult;

    public RpiException(string message, Exception innerException)
        : base(message, innerException) { }
}
