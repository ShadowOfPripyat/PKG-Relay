using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using PkgRelay.Services;
using Xunit;

namespace PkgRelay.Core.Tests;

public sealed class PackageServerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ServerTests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
    [Fact]
    public async Task ServesHeadAndByteRangesAndRemovesRoutes()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pkg");
        try
        {
            var content = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
            await File.WriteAllBytesAsync(path, content);
            await using var server = new PkgHttpServer(new LogService(new SettingsService(_directory)));
            await server.StartAsync(IPAddress.Loopback, GetFreePort());
            var route = server.Register(path);
            var uri = server.GetPackageUri(route);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
            using var head = await http.SendAsync(headRequest);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(content.Length, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(256, 511);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(content[256..512], await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("bytes 256-511/4096", response.Content.Headers.ContentRange?.ToString());

            server.Unregister(route);
            using var missing = await http.GetAsync(uri);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ConcurrentStartsUseASingleServer()
    {
        await using var server = new PkgHttpServer(new LogService(new SettingsService(_directory)));
        var port = GetFreePort();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => server.StartAsync(IPAddress.Loopback, port)));
        Assert.True(server.IsRunning);
        Assert.Equal(port, server.Port);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(IPAddress.Loopback, GetFreePort()));
        await server.StopAsync();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task FailedStartupCanBeRetriedAfterThePortIsReleased()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var server = new PkgHttpServer(new LogService(new SettingsService(_directory)));
        var failure = await Record.ExceptionAsync(() => server.StartAsync(IPAddress.Loopback, port));
        Assert.True(failure is IOException or SocketException, $"Expected a port-binding failure, got {failure}");
        Assert.False(server.IsRunning);
        occupied.Stop();
        await server.StartAsync(IPAddress.Loopback, port);
        Assert.True(server.IsRunning);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
