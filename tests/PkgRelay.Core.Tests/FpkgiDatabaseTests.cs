using System.Net;
using System.Text;
using PkgRelay.Models;
using PkgRelay.Services;
using Xunit;

namespace PkgRelay.Core.Tests;

public sealed class FpkgiDatabaseTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "fpkgi-tests-" + Guid.NewGuid());
    private static string Database(string name) => "{\"DATA\":{\"https://host.test/app.pkg\":{\"name\":\"" + name + "\",\"title_id\":\"TEST00001\",\"min_fw\":\"5.05\"}}}";
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    [Fact]
    public async Task DefaultSourcesPersistAndDownloadBothValidatedDatabases()
    {
        var settings = new SettingsService(folder);
        var urls = new List<string>();
        using var service = new FpkgiDatabaseService(settings, new Handler((request, _) =>
        { urls.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(Json(Database(request.RequestUri.AbsolutePath.EndsWith("DLC.json") ? "DLC" : "Game"))); }));
        await service.DownloadAsync(new[] { FpkgiDatabaseKind.Games, FpkgiDatabaseKind.Dlc });
        Assert.Equal(new[] { FpkgiDatabaseService.DefaultGamesUrl, FpkgiDatabaseService.DefaultDlcUrl }, urls);
        Assert.Equal(1, (await service.InspectAsync(FpkgiDatabaseKind.Games)).Entries);
        Assert.Equal(1, (await service.InspectAsync(FpkgiDatabaseKind.Dlc)).Entries);
        await settings.SaveAsync(); var loaded = new SettingsService(folder); await loaded.LoadAsync();
        Assert.Equal(FpkgiDatabaseService.DefaultGamesUrl, loaded.Current.FpkgiGamesDatabaseUrl);
        Assert.Equal("/user/data/FPKGi/ContentJSONs/DLC.json", FpkgiDatabaseService.RemotePath(FpkgiDatabaseKind.Dlc));
    }
    [Fact]
    public async Task InvalidSecondDownloadPreservesEverySelectedWorkingDatabase()
    {
        var settings = new SettingsService(folder);
        using var service = new FpkgiDatabaseService(settings, new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("DLC.json") ? "{\"DATA\":{}}" : Database("New")))));
        Directory.CreateDirectory(service.Folder);
        var old = Database("Old");
        await File.WriteAllTextAsync(service.PathFor(FpkgiDatabaseKind.Games), old);
        await File.WriteAllTextAsync(service.PathFor(FpkgiDatabaseKind.Dlc), old);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(new[] { FpkgiDatabaseKind.Games, FpkgiDatabaseKind.Dlc }));
        Assert.Equal(old, await File.ReadAllTextAsync(service.PathFor(FpkgiDatabaseKind.Games)));
        Assert.Equal(old, await File.ReadAllTextAsync(service.PathFor(FpkgiDatabaseKind.Dlc)));
        Assert.Empty(Directory.GetFiles(service.Folder, "*.part"));
    }
    [Fact]
    public async Task ImportsValidateAndBackUpAndRestoreWithoutTouchingOtherTypes()
    {
        var settings = new SettingsService(folder); Directory.CreateDirectory(folder);
        using var service = new FpkgiDatabaseService(settings);
        var path = Path.Combine(folder, "input.json");
        await File.WriteAllTextAsync(path, Database("Old")); await service.ImportAsync(FpkgiDatabaseKind.Games, path);
        await File.WriteAllTextAsync(path, Database("New")); await service.ImportAsync(FpkgiDatabaseKind.Games, path);
        Assert.Contains("Old", await File.ReadAllTextAsync(service.PathFor(FpkgiDatabaseKind.Games) + ".bak"));
        await service.RestoreAsync(FpkgiDatabaseKind.Games);
        Assert.Contains("Old", await File.ReadAllTextAsync(service.PathFor(FpkgiDatabaseKind.Games)));
        await File.WriteAllTextAsync(path, "{\"DATA\":{\"file:///C:/x.pkg\":{\"name\":\"Bad\"}}}");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(FpkgiDatabaseKind.Games, path));
        Assert.Contains("Old", await File.ReadAllTextAsync(service.PathFor(FpkgiDatabaseKind.Games)));
        Assert.False(File.Exists(service.PathFor(FpkgiDatabaseKind.Dlc)));
        Assert.Empty(Directory.GetFiles(service.Folder, "*.part"));
    }
    [Fact]
    public async Task GeneratesHomebrewSeparatelyAndRejectsInappropriateDownloadTypes()
    {
        using var service = new FpkgiDatabaseService(new SettingsService(folder));
        await service.GenerateHomebrewAsync(new[] { new HomebrewItem { Name="Homebrew", PackageUrl="https://host.test/tool.pkg", Version="1.0" } });
        Assert.Equal(1, (await service.InspectAsync(FpkgiDatabaseKind.Homebrew)).Entries);
        Assert.False(File.Exists(service.PathFor(FpkgiDatabaseKind.Games)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(new[] { FpkgiDatabaseKind.Homebrew }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync([]));
        Assert.Throws<ArgumentException>(() => FpkgiDatabaseService.SourceUri("file:///C:/x.json"));
    }
    [Fact]
    public async Task CancellationRemovesIncompleteFilesAndKeepsExistingDatabase()
    {
        var settings = new SettingsService(folder); using var cancel = new CancellationTokenSource();
        using var service = new FpkgiDatabaseService(settings, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new CancelOnReadStream(Encoding.UTF8.GetBytes(Database("New")), cancel)) })));
        Directory.CreateDirectory(service.Folder);
        await File.WriteAllTextAsync(service.PathFor(FpkgiDatabaseKind.Games), Database("Old"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(new[] { FpkgiDatabaseKind.Games }, token: cancel.Token));
        Assert.Contains("Old", await File.ReadAllTextAsync(service.PathFor(FpkgiDatabaseKind.Games)));
        Assert.Empty(Directory.GetFiles(service.Folder, "*.part"));
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class CancelOnReadStream(byte[] bytes, CancellationTokenSource cancel) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var count = await base.ReadAsync(buffer, token); cancel.Cancel(); return count; }
    }
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
