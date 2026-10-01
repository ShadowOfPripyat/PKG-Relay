using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PkgRelay.Services;

public sealed class PkgHttpServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, string> _files =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    public bool IsRunning => _app is not null;
    public IPAddress? Address { get; private set; }
    public int Port { get; private set; }

    public PkgHttpServer(LogService log)
    {
        _log = log;
    }

    public string Register(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("PKG file not found.", fullPath);

        if (!string.Equals(
                Path.GetExtension(fullPath),
                ".pkg",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only .pkg files can be served.");
        }

        var id = GetRouteId(fullPath);
        _files[id] = fullPath;
        return id;
    }

    public void Unregister(string id)
    {
        _files.TryRemove(id, out _);
    }

    public Uri GetPackageUri(string id)
    {
        if (!IsRunning || Address is null)
            throw new InvalidOperationException("The PKG server is not running.");

        return new Uri($"http://{Address}:{Port}/pkg/{id}");
    }

    public async Task StartAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null && (Port != port || !address.Equals(Address)))
                throw new InvalidOperationException(
                    $"Package server is already running on {Address}:{Port}; it cannot move to {address}:{port} while routes are active.");

            await StartCoreAsync(address, port, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        if (_app is not null)
            return;

        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, port);
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });

        var app = builder.Build();

        app.MapGet("/health", () =>
            Results.Ok(new
            {
                status = "ok",
                packages = _files.Count
            }));

        app.MapMethods(
            "/pkg/{id}",
            ["GET", "HEAD"],
            (string id) =>
            {
                if (!_files.TryGetValue(id, out var path) ||
                    !File.Exists(path))
                {
                    return Results.NotFound();
                }

                return Results.File(
                    path,
                    contentType: "application/octet-stream",
                    enableRangeProcessing: true);
            });

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        _app = app;
        Address = address;
        Port = port;

        await _log.InfoAsync($"PKG server listening on http://{address}:{port}");
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var app = _app;
        if (app is null)
            return;

        _app = null;

        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
            await _log.InfoAsync("PKG server stopped.");
            Address = null;
            Port = 0;
        }
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }

    internal static string GetRouteId(string path)
    {
        var fingerprint = $"{path.ToUpperInvariant()}|{new FileInfo(path).Length}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }
}
