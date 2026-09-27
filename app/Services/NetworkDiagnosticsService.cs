using System.Net;
using System.Net.Sockets;
using PkgRelay.Models;

namespace PkgRelay.Services;

public sealed class NetworkDiagnosticsService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly NetworkService _network;
    private readonly PkgHttpServer _server;
    private readonly Ps4RpiClient _rpi;
    private readonly HttpClient _http;

    public NetworkDiagnosticsService(
        SettingsService settings,
        NetworkService network,
        PkgHttpServer server,
        Ps4RpiClient rpi)
    {
        _settings = settings;
        _network = network;
        _server = server;
        _rpi = rpi;

        _http = new HttpClient(
            new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(2)
            })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public async Task<NetworkDiagnosticReport> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<NetworkDiagnosticItem>();
        var settings = _settings.Current;

        if (string.IsNullOrWhiteSpace(settings.Ps4Address) ||
            !IPAddress.TryParse(settings.Ps4Address, out var ps4Address) ||
            ps4Address.AddressFamily != AddressFamily.InterNetwork)
        {
            results.Add(new(
                DiagnosticSeverity.Error,
                "PS4 address",
                "A valid PS4 IPv4 address is not configured."));

            return new NetworkDiagnosticReport(results);
        }

        results.Add(new(
            DiagnosticSeverity.Success,
            "PS4 address",
            $"{ps4Address}:{settings.RpiPort}"));

        var rpiReachable =
            await _rpi.TestConnectionAsync(
                cancellationToken);

        results.Add(new(
            rpiReachable
                ? DiagnosticSeverity.Success
                : DiagnosticSeverity.Error,
            "Remote Package Installer",
            rpiReachable
                ? $"TCP {settings.RpiPort} is reachable."
                : $"TCP {settings.RpiPort} did not accept a connection. Keep RPI in the foreground and verify the PS4 address."));

        IPAddress? localAddress = null;

        try
        {
            localAddress =
                _network.ResolveLocalAddress(
                    settings.Ps4Address,
                    settings.RpiPort,
                    settings.LocalAddress);

            results.Add(new(
                DiagnosticSeverity.Success,
                "PC route",
                $"Packages will be served from {localAddress}:{settings.HttpPort}."));
        }
        catch (Exception ex)
        {
            results.Add(new(
                DiagnosticSeverity.Error,
                "PC route",
                ex.Message));
        }

        if (localAddress is not null)
        {
            if (_server.IsRunning)
            {
                var endpointMatches =
                    _server.Address?.Equals(localAddress) == true &&
                    _server.Port == settings.HttpPort;

                results.Add(new(
                    endpointMatches
                        ? DiagnosticSeverity.Success
                        : DiagnosticSeverity.Warning,
                    "Package server",
                    endpointMatches
                        ? $"Already listening on {_server.Address}:{_server.Port}."
                        : $"Currently listening on {_server.Address}:{_server.Port}; settings resolve to {localAddress}:{settings.HttpPort}."));

                if (_server.Address is not null)
                {
                    try
                    {
                        using var response =
                            await _http.GetAsync(
                                new Uri(
                                    $"http://{_server.Address}:{_server.Port}/health"),
                                cancellationToken);

                        results.Add(new(
                            response.IsSuccessStatusCode
                                ? DiagnosticSeverity.Success
                                : DiagnosticSeverity.Warning,
                            "Package server health",
                            response.IsSuccessStatusCode
                                ? "Local /health endpoint responded successfully."
                                : $"Local /health endpoint returned HTTP {(int)response.StatusCode}."));
                    }
                    catch (Exception ex)
                        when (!cancellationToken.IsCancellationRequested)
                    {
                        results.Add(new(
                            DiagnosticSeverity.Warning,
                            "Package server health",
                            ex.Message));
                    }
                }
            }
            else
            {
                var bindResult =
                    CanBind(
                        localAddress,
                        settings.HttpPort,
                        out var bindError);

                results.Add(new(
                    bindResult
                        ? DiagnosticSeverity.Success
                        : DiagnosticSeverity.Error,
                    "HTTP port",
                    bindResult
                        ? $"{localAddress}:{settings.HttpPort} is available for the package server."
                        : bindError));
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.PackageFolder))
        {
            results.Add(new(
                Directory.Exists(settings.PackageFolder)
                    ? DiagnosticSeverity.Success
                    : DiagnosticSeverity.Warning,
                "Default PKG folder",
                Directory.Exists(settings.PackageFolder)
                    ? settings.PackageFolder
                    : $"Folder does not currently exist: {settings.PackageFolder}"));
        }

        results.Add(new(
            DiagnosticSeverity.Warning,
            "PS4 → PC reachability",
            "Windows cannot prove the console can reach the package server until the PS4 requests a PKG. If a transfer starts and then fails, check Windows Firewall and LAN isolation."));

        return new NetworkDiagnosticReport(results);
    }

    private static bool CanBind(
        IPAddress address,
        int port,
        out string detail)
    {
        TcpListener? listener = null;

        try
        {
            listener = new TcpListener(address, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            detail = "";
            return true;
        }
        catch (Exception ex)
        {
            detail =
                $"Unable to bind {address}:{port}: {ex.Message}";
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
