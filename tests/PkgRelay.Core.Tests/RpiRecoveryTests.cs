using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PkgRelay.Models;
using PkgRelay.Services;
using Xunit;

namespace PkgRelay.Core.Tests;

public sealed class RpiRecoveryTests
{
    [Theory]
    [InlineData(500, "\"error_result\":-2137456619", "\"error_result\":-2143088536")]
    [InlineData(200, "\"error\":0x80990015", "\"error\":0x80431068")]
    [InlineData(500, "\"error_code\":\"0x80990015\"", "\"error_result\":\"0x80431068\"")]
    public async Task RetriesFailedDuplicateWithCurrentUrls(int status, string duplicate, string progress)
    {
        await using var fixture = await Fixture.Create(status, duplicate, progress);
        var taskId = await fixture.Install();
        Assert.Equal(387, taskId);
        Assert.Equal(new[] { "install", "find_task", "get_task_progress", "unregister_task", "install" }, fixture.Routes);
        Assert.Equal(fixture.InstallBodies[0], fixture.InstallBodies[1]);
        Assert.Contains("http://192.0.2.17:8080/pkg/current", fixture.InstallBodies[1]);
        Assert.Contains("\"sub_type\":6", fixture.FindBody);
    }

    [Theory]
    [InlineData("\"error_result\":0")]
    [InlineData("\"transferred_total\":123")]
    [InlineData("\"error_result\":\"invalid\"")]
    public async Task DoesNotDeleteTaskWithoutConfirmedFailure(string progress)
    {
        await using var fixture = await Fixture.Create(500, "\"error_result\":-2137456619", progress);
        var error = await Assert.ThrowsAsync<RpiException>(() => fixture.Install());
        Assert.Contains("not confirmed failed", error.Message);
        Assert.DoesNotContain("unregister_task", fixture.Routes);
        Assert.Single(fixture.InstallBodies);
    }

    [Fact]
    public async Task DoesNotRetryWhenUnregistrationFails()
    {
        await using var fixture = await Fixture.Create(500, "\"error_result\":-2137456619", "\"error_result\":-1");
        fixture.UnregisterFails = true;
        await Assert.ThrowsAsync<RpiException>(() => fixture.Install());
        Assert.Single(fixture.InstallBodies);
    }

    [Fact]
    public async Task StopsAfterOneRetry()
    {
        await using var fixture = await Fixture.Create(500, "\"error_result\":-2137456619", "\"error_result\":-1");
        fixture.SecondInstallFails = true;
        await Assert.ThrowsAsync<RpiException>(() => fixture.Install());
        Assert.Equal(2, fixture.InstallBodies.Count);
        Assert.Equal(1, fixture.Routes.Count(route => route == "unregister_task"));
    }

    [Fact]
    public async Task OtherRegistrationErrorsDoNotRemoveAnyTask()
    {
        await using var fixture = await Fixture.Create(500, "\"error_result\":-456", "\"error_result\":-1");
        await Assert.ThrowsAsync<RpiException>(() => fixture.Install());
        Assert.Equal(new[] { "install" }, fixture.Routes);
    }

    [Fact]
    public async Task LegacyInstallerUsesTheSameRecovery()
    {
        await using var fixture = await Fixture.Create(200, "\"error\":0x80990015", "\"error\":0x80431068", resident: false);
        Assert.Equal(387, await fixture.Install());
        Assert.Contains("unregister_task", fixture.Routes);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public async Task FindsOnlyTheRequestedPackageSubtype(int subType)
    {
        await using var fixture = await Fixture.Create(500, "\"error_result\":-2137456619", "\"error_result\":-1");
        fixture.SubType = subType;
        Assert.Equal(387, await fixture.Install());
        using var found = JsonDocument.Parse(fixture.FindBody);
        Assert.Equal(subType, found.RootElement.GetProperty("sub_type").GetInt32());
    }

    [Fact]
    public async Task MissingContentIdDoesNotRemoveAnyTask()
    {
        await using var fixture = await Fixture.Create(500, "\"error_result\":-2137456619", "\"error_result\":-1");
        fixture.RecoveryContentId = "";
        await Assert.ThrowsAsync<RpiException>(() => fixture.Install());
        Assert.Equal(new[] { "install" }, fixture.Routes);
    }

    [Fact]
    public async Task UnconfirmedUnregistrationDoesNotRetry()
    {
        await using var fixture = await Fixture.Create(500, "\"error_result\":-2137456619", "\"error_result\":-1");
        fixture.UnregisterUnconfirmed = true;
        await Assert.ThrowsAsync<RpiException>(() => fixture.Install());
        Assert.Single(fixture.InstallBodies);
    }

    [Fact]
    public async Task CancellationDoesNotRemoveAnyTask()
    {
        await using var fixture = await Fixture.Create(500, "\"error_result\":-2137456619", "\"error_result\":-1");
        using var cancel = new CancellationTokenSource();
        fixture.CancelDuringProgress = cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Install(cancel.Token));
        Assert.DoesNotContain("unregister_task", fixture.Routes);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rpi-tests-" + Guid.NewGuid());
        private WebApplication server = null!;
        private Ps4RpiClient client = null!;
        internal List<string> Routes { get; } = [];
        internal List<string> InstallBodies { get; } = [];
        internal string FindBody = "";
        internal bool UnregisterFails;
        internal bool SecondInstallFails;
        internal bool UnregisterUnconfirmed;
        internal int SubType = 6;
        internal string RecoveryContentId = "EP3585-CUSA18027_00-TRAILMAKERS00000";
        internal CancellationTokenSource? CancelDuringProgress;

        internal static async Task<Fixture> Create(int status, string duplicate, string progress, bool resident = false)
        {
            var fixture = new Fixture();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            fixture.server = builder.Build();
            fixture.server.MapGet("/api/health", () => Results.Json(new { status = "ok", service = "Orbis Toolz" }));
            fixture.server.MapPost("/api/{route}", async context =>
            {
                var route = (string)context.Request.RouteValues["route"]!;
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                fixture.Routes.Add(route);
                context.Response.ContentType = "application/json";
                string reply;
                switch (route)
                {
                    case "install":
                        fixture.InstallBodies.Add(body);
                        if (fixture.InstallBodies.Count == 1 || fixture.SecondInstallFails)
                        {
                            context.Response.StatusCode = status;
                            reply = "{\"status\":\"error\"," + duplicate + "}";
                        }
                        else reply = "{\"status\":\"success\",\"task_id\":387}";
                        break;
                    case "find_task":
                        fixture.FindBody = body;
                        using (var parsed = JsonDocument.Parse(body))
                            Assert.Equal("EP3585-CUSA18027_00-TRAILMAKERS00000", parsed.RootElement.GetProperty("content_id").GetString());
                        reply = "{\"status\":\"success\",\"task_id\":386}";
                        break;
                    case "get_task_progress":
                    case "unregister_task":
                        using (var parsed = JsonDocument.Parse(body))
                            Assert.Equal(386, parsed.RootElement.GetProperty("task_id").GetInt32());
                        if (route == "get_task_progress")
                        {
                            fixture.CancelDuringProgress?.Cancel();
                            reply = "{\"status\":\"success\"," + progress + "}";
                        }
                        else if (fixture.UnregisterFails)
                        {
                            context.Response.StatusCode = 500;
                            reply = "{\"status\":\"error\",\"error_result\":-12}";
                        }
                        else reply = fixture.UnregisterUnconfirmed ? "{}" : "{\"status\":\"success\"}";
                        break;
                    default: throw new InvalidOperationException(route);
                }
                await context.Response.WriteAsync(reply);
            });
            await fixture.server.StartAsync();
            var address = fixture.server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var settings = new SettingsService(fixture.directory);
            settings.Current.Ps4Address = "127.0.0.1";
            settings.Current.RpiPort = new Uri(address).Port;
            fixture.client = new Ps4RpiClient(settings, new LogService(settings));
            return fixture;
        }

        internal Task<int> Install(CancellationToken cancellationToken = default) => client.InstallDirectAsync(
            [new Uri("http://192.0.2.17:8080/pkg/current")], cancellationToken,
            contentId: RecoveryContentId, taskSubType: SubType);
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await server.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
