using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using PkgRelay.Models;
using PkgRelay.Services;
using Xunit;

namespace PkgRelay.Core.Tests;

public sealed class InstalledPackageVerifierTests
{
    private const string ContentId = "UP0000-CUSA12345_00-DLC0000000000001";

    [Theory]
    [InlineData(true, false, InstalledPackageState.Installed)]
    [InlineData(false, false, InstalledPackageState.NotInstalled)]
    [InlineData(false, true, InstalledPackageState.Installing)]
    [InlineData(true, true, InstalledPackageState.Installing)]
    public async Task InstalledStateRequiresAConfirmedIdentityAndNoActiveInstall(bool installed, bool installing, InstalledPackageState expected)
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = JsonSerializer.Serialize(new { status = "success", content_id = ContentId, installed, installing });
        Assert.Equal(expected, await fixture.Verifier.CheckAsync(fixture.Job));
        using var request = JsonDocument.Parse(fixture.Handler.Body);
        Assert.Equal(ContentId, request.RootElement.GetProperty("content_id").GetString());
        Assert.Equal(new string('a', 64), request.RootElement.GetProperty("package_digest").GetString());
        Assert.Equal("PS4AC", request.RootElement.GetProperty("package_type").GetString());
        Assert.False(request.RootElement.GetProperty("is_patch").GetBoolean());
        Assert.Equal("test-token", fixture.Handler.Token);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"success\",\"exists\":true}")]
    [InlineData("{\"status\":\"success\",\"content_id\":\"wrong-DLC\",\"installed\":true,\"installing\":false}")]
    [InlineData("{\"status\":\"error\",\"content_id\":\"UP0000-CUSA12345_00-DLC0000000000001\",\"installed\":true,\"installing\":false}")]
    [InlineData("<html>Not Found</html>")]
    public async Task IncompleteWrongOrOldApiResponsesDoNotClaimCompletion(string response)
    {
        using var fixture = new Fixture(); fixture.Handler.Reply = response;
        Assert.Equal(InstalledPackageState.Unknown, await fixture.Verifier.CheckAsync(fixture.Job));
    }

    [Theory]
    [InlineData(403)] [InlineData(404)] [InlineData(500)] [InlineData(503)]
    public async Task AnUnavailableUnpairedOrOldServiceIsUnknown(int code)
    {
        using var fixture = new Fixture(); fixture.Handler.Code = (HttpStatusCode)code;
        Assert.Equal(InstalledPackageState.Unknown, await fixture.Verifier.CheckAsync(fixture.Job));
    }

    [Fact]
    public async Task UnknownDigestOrMismatchedLocalFileDoesNotQueryTheConsole()
    {
        using var fixture = new Fixture();
        fixture.Job.ContentId = "different";
        Assert.Equal(InstalledPackageState.Unknown, await fixture.Verifier.CheckAsync(fixture.Job));
        fixture.Job.ContentId = ContentId;
        var header = File.ReadAllBytes(fixture.Job.FilePath); header.AsSpan(0xfe0, 32).Clear();
        File.WriteAllBytes(fixture.Job.FilePath, header);
        Assert.Equal(InstalledPackageState.Unknown, await fixture.Verifier.CheckAsync(fixture.Job));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task PatchIdentityUsesThePatchPackageAndCallerCancellationPropagates()
    {
        using var fixture = new Fixture();
        var header = File.ReadAllBytes(fixture.Job.FilePath);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x74), 0x1a);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x78), 0x40000000);
        File.WriteAllBytes(fixture.Job.FilePath, header);
        await fixture.Verifier.CheckAsync(fixture.Job);
        using var request = JsonDocument.Parse(fixture.Handler.Body);
        Assert.True(request.RootElement.GetProperty("is_patch").GetBoolean());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Verifier.CheckAsync(fixture.Job, cancelled.Token));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "InstalledPackageTests-" + Guid.NewGuid());
        public Handler Handler { get; } = new();
        public InstallJob Job { get; }
        public InstalledPackageVerifier Verifier { get; }
        public Fixture()
        {
            Directory.CreateDirectory(directory);
            var settings = new SettingsService(directory);
            settings.Current.Ps4Address = "192.0.2.1";
            settings.Current.ResidentRpiToken = "test-token";
            var header = new byte[4096];
            BinaryPrimitives.WriteUInt32BigEndian(header, 0x7f434e54);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x74), 0x1b);
            Encoding.ASCII.GetBytes(ContentId).CopyTo(header, 0x40);
            header.AsSpan(0xfe0, 32).Fill(0xaa);
            var path = Path.Combine(directory, "test.pkg"); File.WriteAllBytes(path, header);
            Job = new InstallJob { FilePath = path, ContentId = ContentId, Kind = PkgKind.Dlc, SourceKind = InstallSourceKind.LocalFile };
            Verifier = new InstalledPackageVerifier(settings, Handler);
        }
        public void Dispose() { Verifier.Dispose(); Directory.Delete(directory, true); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string Reply = "{}", Body = "", Token = "";
        public int Calls;
        public HttpStatusCode Code = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            Assert.Equal("/api/get_install_status", request.RequestUri!.AbsolutePath);
            Token = request.Headers.GetValues("X-Orbis-Token").Single();
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(Code) { Content = new StringContent(Reply) };
        }
    }
}
