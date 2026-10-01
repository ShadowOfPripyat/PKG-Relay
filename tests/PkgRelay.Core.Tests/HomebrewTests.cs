using System.Net;
using System.Text;
using System.Text.Json;
using PkgRelay.Models;
using PkgRelay.Services;
using Xunit;
using Microsoft.Data.Sqlite;
namespace PkgRelay.Core.Tests;
public sealed class HomebrewTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "homebrew-tests-" + Guid.NewGuid());
    private SettingsService Settings()
    {
        var settings = new SettingsService(directory);
        settings.Current.HomebrewDownloadFolder = Path.Combine(directory, "downloads");
        return settings;
    }
    [Fact]
    public void ParsesUrlKeyedFpkgiCatalogAndCaseInsensitiveStoreFields()
    {
        using var json = JsonDocument.Parse("""
        {"DATA":{"https://host.test/app.pkg":{"NAME":"Tool","Author":"Dev","Size":"1048576","apptype":"Utility","id":"APOL00004","desc":"First","desc_1":"Second","cover_url":"/icon.png","version":"2.00"}}}
        """);
        var item = Assert.Single(HomebrewCatalog.Parse(json.RootElement, new Uri("https://host.test/catalog.json")));
        Assert.Equal("Tool", item.Name);
        Assert.Equal("Dev", item.Author);
        Assert.Equal(1048576, item.Size);
        Assert.Equal("Utility", item.Category);
        Assert.Equal("APOL00004", item.TitleId);
        Assert.Equal("First\nSecond", item.Description);
        Assert.Equal("https://host.test/icon.png", item.ImageUrl);
    }
    [Theory]
    [InlineData("[{\"name\":\"One\",\"package_url\":\"/one.pkg\"}]")]
    [InlineData("{\"items\":[{\"title\":\"One\",\"download_url\":\"/one.pkg\"}]}")]
    [InlineData("{\"data\":{\"packages\":[{\"name\":\"One\",\"package\":\"/one.pkg\"}]}}")]
    public void ParsesExistingCatalogShapesAndRelativeUrls(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        var item = Assert.Single(HomebrewCatalog.Parse(json.RootElement, new Uri("https://host.test/catalog.json")));
        Assert.Equal("One", item.Name);
        Assert.Equal("https://host.test/one.pkg", item.PackageUrl);
    }
    [Fact]
    public void InvalidEntriesAndUnsafeSchemesCannotBecomeInstallLinks()
    {
        using var json = JsonDocument.Parse("""
        [null,0,"text",{"name":"Bad","url":"file:///C:/private.pkg"},{"url":"javascript:alert(1)"},{"url":"https://host.test/good.pkg"},{"url":"https://host.test/good.pkg"}]
        """);
        Assert.Single(HomebrewCatalog.Parse(json.RootElement, new Uri("https://host.test/")));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"DATA\":{}}")]
    public void EmptyCatalogsAreValidAndDoNotTriggerCacheFallback(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Empty(HomebrewCatalog.Parse(json.RootElement, new Uri("https://host.test/")));
    }
    [Fact]
    public void ReleaseAssetsExcludeElfAndPs5AndKeepPackageMetadata()
    {
        using var json = JsonDocument.Parse("""
        {"tag_name":"v2.3.2","name":"Apollo 2.3.2","published_at":"2026-01-20T00:00:00Z","assets":[
        {"name":"homebrew.elf","browser_download_url":"https://host.test/a.elf"},
        {"name":"ps5.pkg","browser_download_url":"https://host.test/ps5.pkg"},
        {"name":"IV0000-APOL00004_00-APOLLO0000000PS4.pkg","browser_download_url":"https://host.test/ps4.pkg","size":22675456}]}
        """);
        var item = Assert.Single(HomebrewCatalog.FromRelease(json.RootElement, "Apollo", "bucanero", "Utilities"));
        Assert.Equal("APOL00004", item.TitleId);
        Assert.Equal("2.3.2", item.Version);
        Assert.Equal(22675456, item.Size);
        Assert.Equal(2026, item.ReleaseDate!.Value.Year);
    }
    [Theory]
    [InlineData("17-02-2025")]
    [InlineData("2025-02-17")]
    [InlineData("2025-02-17T00:00:00Z")]
    public void StoreAndIsoReleaseDatesSortUsingTheActualDate(string date)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new { name="App", url="https://host.test/a.pkg", release=date } }));
        var item = Assert.Single(HomebrewCatalog.Parse(json.RootElement, new Uri("https://host.test/")));
        Assert.Equal(new DateTimeOffset(2025,2,17,0,0,0,TimeSpan.Zero), item.ReleaseDate);
    }
    [Fact]
    public void SearchCategoryAndSortingCombineWithoutChangingTheCatalog()
    {
        var old = new HomebrewItem { Name="Alpha", Author="Dev", Description="save tool", Category="Utilities", Size=10, ReleaseDate=DateTimeOffset.Parse("2025-01-01") };
        var recent = new HomebrewItem { Name="Beta", Author="Dev", TitleId="APOL00004", Category="Utilities", Size=20, ReleaseDate=DateTimeOffset.Parse("2026-01-01") };
        var unknown = new HomebrewItem { Name="Zero", Category="Games" };
        HomebrewItem[] catalog = [unknown, recent, old];
        Assert.Equal(new[] { old, recent }, HomebrewCatalog.Query(catalog, "", "utilities", 0));
        Assert.Equal(new[] { recent, old }, HomebrewCatalog.Query(catalog, "DEV", "Utilities", 2));
        Assert.Single(HomebrewCatalog.Query(catalog, "dev save", "", 0));
        Assert.Equal(recent, Assert.Single(HomebrewCatalog.Query(catalog, "apol00004", "", 0)));
        Assert.Equal(new[] { old, recent, unknown }, HomebrewCatalog.Query(catalog, "", "", 3));
        Assert.Equal(new[] { recent, old, unknown }, HomebrewCatalog.Query(catalog, "", "", 4));
        Assert.Equal(unknown, catalog[0]);
    }
    [Fact]
    public async Task LegacyPkgZoneRootUsesOfficialFeedsInsteadOfTheBlockedWebsite()
    {
        var settings = Settings(); settings.Current.HomebrewApiUrl = "http://api.pkg-zone.com/";
        var calls = new List<string>();
        using var service = new HomebrewService(settings, new Handler((request, _) =>
        {
            lock (calls) calls.Add(request.RequestUri!.Host);
            return Task.FromResult(Json("{\"tag_name\":\"v1\",\"assets\":[{\"name\":\"ps4.pkg\",\"browser_download_url\":\"https://host.test/ps4.pkg\",\"size\":4096}]}"));
        }));
        Assert.NotEmpty(await service.LoadAsync());
        Assert.All(calls, host => Assert.Equal("api.github.com", host));
        Assert.Equal("Official project releases", service.ProviderName);
        Assert.Contains("PKG-Zone", service.Notice);
    }
    [Fact]
    public async Task CacheSurvivesProviderOutageAndDoesNotLeakAcrossProviders()
    {
        var settings = Settings(); settings.Current.HomebrewApiUrl = "https://host.test/catalog.json";
        var down = false;
        using var service = new HomebrewService(settings, new Handler((_, _) => Task.FromResult(down ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json("[{\"name\":\"App\",\"url\":\"https://host.test/a.pkg\"}]"))));
        Assert.Single(await service.LoadAsync());
        down = true;
        Assert.Single(await service.LoadAsync());
        Assert.Contains("last saved", service.Notice);
        settings.Current.HomebrewApiUrl = "https://other.test/catalog.json";
        var error = await Assert.ThrowsAsync<IOException>(() => service.LoadAsync());
        Assert.Contains("403", error.Message);
    }
    [Fact]
    public async Task HtmlCatalogGetsAnActionableErrorRatherThanSilentEmptyResults()
    {
        var settings = Settings(); settings.Current.HomebrewApiUrl = "https://host.test/";
        using var service = new HomebrewService(settings, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("<html>browser challenge</html>", Encoding.UTF8, "text/html") })));
        var error = await Assert.ThrowsAsync<IOException>(() => service.LoadAsync());
        Assert.Contains("webpage", error.Message);
    }
    [Fact]
    public async Task CancellationDoesNotReturnTheCachedCatalog()
    {
        var settings = Settings(); settings.Current.HomebrewApiUrl = "https://host.test/catalog.json";
        using var service = new HomebrewService(settings, new Handler((_, _) => Task.FromResult(Json("[{\"url\":\"https://host.test/a.pkg\"}]"))));
        await service.LoadAsync();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(cancelled.Token));
    }
    [Fact]
    public async Task DownloadClosesTheWindowsFileBeforeRenameAndKeepsExistingPackages()
    {
        var settings = Settings();
        var bytes = Package();
        using var download = new DownloadService(settings, new LogService(settings), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(bytes) })));
        var first = await download.DownloadAsync("https://host.test/a.pkg", "Tool");
        var second = await download.DownloadAsync("https://host.test/a.pkg", "Tool");
        Assert.NotEqual(first, second);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(second));
        Assert.Empty(Directory.GetFiles(settings.Current.HomebrewDownloadFolder, "*.part"));
    }
    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public async Task DownloadRejectsChallengePagesAndRemovesPartialFiles(string type)
    {
        var settings = Settings();
        using var download = new DownloadService(settings, new LogService(settings), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("<html>challenge</html>", Encoding.UTF8, type) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => download.DownloadAsync("https://host.test/a.pkg", "Tool"));
        Assert.Empty(Directory.GetFiles(settings.Current.HomebrewDownloadFolder));
    }
    [Fact]
    public async Task CancelledDownloadRemovesTheOpenPartialFile()
    {
        var settings = Settings();
        using var token = new CancellationTokenSource();
        using var download = new DownloadService(settings, new LogService(settings), new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StreamContent(new CancelOnReadStream(Package(), token)) })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.DownloadAsync("https://host.test/a.pkg", "Tool", cancellationToken:token.Token));
        Assert.Empty(Directory.GetFiles(settings.Current.HomebrewDownloadFolder));
    }
    [Fact]
    public async Task ConcurrentPreferenceSavesKeepAReadableCompleteSettingsFile()
    {
        var settings = Settings(); settings.Current.HomebrewViewMode=2; settings.Current.HomebrewSortMode=4; settings.Current.HomebrewCategoryFilter="File managers";
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => settings.SaveAsync()));
        var restored = new SettingsService(directory); await restored.LoadAsync();
        Assert.Equal(2, restored.Current.HomebrewViewMode);
        Assert.Equal(4, restored.Current.HomebrewSortMode);
        Assert.Equal("File managers", restored.Current.HomebrewCategoryFilter);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content=new StringContent(json, Encoding.UTF8, "application/json") };
    [Fact]
    public void GroupsVersionsAndSelectsNewestStableOfficialPackage()
    {
        var old = new HomebrewItem { Name="Apollo Save Tool", Version="2.9", PackageUrl="https://github.com/old.pkg", IsOfficial=true };
        var recent = new HomebrewItem { Name="Apollo Save Tool", Version="2.10", PackageUrl="https://github.com/new.pkg", IsOfficial=true };
        var mirror = new HomebrewItem { Name="PS4 Apollo Save Tool", Version="9.9", PackageUrl="https://mirror.test/a.pkg" };
        var preview = new HomebrewItem { Name="Apollo", Version="3.0", PackageUrl="https://github.com/preview.pkg", IsOfficial=true, IsPrerelease=true };
        var app = Assert.Single(HomebrewCatalog.Group(new[] { old, preview, mirror, recent }));
        Assert.Same(recent, app.SelectedVersion);
        Assert.Equal(4, app.Versions.Count);
        Assert.Single(HomebrewCatalog.QueryApps(new[] { app }, "2.9", "", 0));
        var changed = 0; app.PropertyChanged += (_, _) => changed++;
        app.SelectedVersion = old;
        Assert.Equal(1, changed);
        Assert.Same(old, app.SelectedVersion);
    }
    [Fact]
    public void ReleaseDatesHandleProjectsThatChangedTheirVersionNumberFormat()
    {
        var old = new HomebrewItem { Name="FTP Client", Version="1.01", ReleaseDate=DateTimeOffset.Parse("2022-12-16"), IsOfficial=true, PackageUrl="https://host.test/old.pkg" };
        var latest = new HomebrewItem { Name="FTP Client", Version="1.0.8", ReleaseDate=DateTimeOffset.Parse("2023-11-28"), IsOfficial=true, PackageUrl="https://host.test/new.pkg" };
        Assert.Same(latest, Assert.Single(HomebrewCatalog.Group(new[] { old, latest })).SelectedVersion);
    }
    [Fact]
    public void FirmwareAndSourceMetadataSurviveParsingAndVersionSelection()
    {
        using var json = JsonDocument.Parse("""[{"name":"Tool","url":"https://host.test/tool.pkg","min_fw":"5.05+","source_name":"Official developer","release":"2026-01-20"}]""");
        var item = Assert.Single(HomebrewCatalog.Parse(json.RootElement, new Uri("https://host.test/")));
        Assert.Equal("5.05+", item.Firmware);
        Assert.Contains("5.05+", item.VersionLabel);
        Assert.Contains("Official developer", item.VersionLabel);
        Assert.Contains("not specified", new HomebrewItem().FirmwareText);
        Assert.Single(HomebrewCatalog.Query(new[] { item }, "5.05", "", 0));
    }
    [Fact]
    public void DatabaseImporterReadsReadonlyStoreSchemaAndDerivesDocumentedLinks()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.db");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE homebrews(id TEXT,name TEXT,version TEXT,min_fw TEXT,icon TEXT,size INTEGER); INSERT INTO homebrews VALUES('LAPY20009','PS4 Xplorer 2.0','2.06','5.05–12.52','https://host.test/icon.png',4096);";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(path);
        var item = Assert.Single(HomebrewStoreDatabase.Read(path));
        Assert.Equal("https://api.pkg-zone.com/download.php?tid=LAPY20009", item.PackageUrl);
        Assert.Equal("5.05–12.52", item.Firmware);
        Assert.Equal("PKG-Zone", item.SourceName);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task ImportedFullCatalogPersistsAndMergesDuringFeedOutages()
    {
        var settings=Settings(); var failed=false;
        using var service=new HomebrewService(settings, new Handler((_, _) => Task.FromResult(failed ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json("{\"tag_name\":\"v1\",\"assets\":[{\"name\":\"ps4.pkg\",\"browser_download_url\":\"https://host.test/ps4.pkg\"}]}"))));
        await service.LoadAsync();
        var path=Path.Combine(directory,"import.json");
        await File.WriteAllTextAsync(path,"[{\"name\":\"Full Store Extra\",\"url\":\"https://host.test/extra.pkg\"}]");
        Assert.Equal(1, await service.ImportCatalogAsync(path));
        Assert.Contains(await service.LoadAsync(), x=>x.Name=="Full Store Extra");
        failed=true;
        Assert.Contains(await service.LoadAsync(), x=>x.Name=="Full Store Extra");
        await File.WriteAllTextAsync(path,"[]");
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.ImportCatalogAsync(path));
        Assert.Contains(await service.LoadAsync(), x=>x.Name=="Full Store Extra");
    }
    [Fact]
    public async Task ImportedCatalogWorksWithoutAnyPriorOfficialFeedCache()
    {
        var settings = Settings(); Directory.CreateDirectory(directory);
        using var service = new HomebrewService(settings, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var path = Path.Combine(directory, "saved.json");
        await File.WriteAllTextAsync(path, "[{\"name\":\"Offline Store\",\"url\":\"https://host.test/app.pkg\"}]");
        await service.ImportCatalogAsync(path);
        Assert.Single(await service.LoadAsync());
        Assert.Contains("could not refresh", service.Notice);
    }
    [Fact]
    public void FpkgiExportUsesTheSelectedPackageAndSupportedDatabaseMetadata()
    {
        var item = new HomebrewItem { Name="App", Version="1.2", PackageUrl="https://host.test/app.pkg", Firmware="5.05–12.52 (developer listed)", ReleaseDate=DateTimeOffset.Parse("2026-01-28"), ImageUrl="https://host.test/icon.png" };
        using var json = JsonDocument.Parse(HomebrewCatalog.FpkgiDatabase(new[] { item, item }));
        var entries = json.RootElement.GetProperty("DATA");
        Assert.Single(entries.EnumerateObject());
        Assert.Equal("5.05", entries.GetProperty(item.PackageUrl).GetProperty("min_fw").GetString());
        Assert.Equal("28-01-2026", entries.GetProperty(item.PackageUrl).GetProperty("release").GetString());
        Assert.Equal(item.ImageUrl, entries.GetProperty(item.PackageUrl).GetProperty("cover_url").GetString());
        Assert.Single(HomebrewCatalog.Parse(json.RootElement, new Uri("https://api.pkg-zone.com/")));
        Assert.Throws<InvalidOperationException>(() => HomebrewCatalog.FpkgiDatabase([]));
    }
    [Fact]
    public async Task BrowserDownloadsAreValidatedBeforeJoiningThePackageLibrary()
    {
        var settings=Settings(); Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"browser.pkg"); await File.WriteAllBytesAsync(path,Package());
        using var download=new DownloadService(settings,new LogService(settings));
        var accepted=await download.AcceptBrowserDownloadAsync(path,"Browser Tool");
        Assert.Equal(Package(),await File.ReadAllBytesAsync(accepted));
        await File.WriteAllTextAsync(path,"<html>verification</html>");
        await Assert.ThrowsAsync<InvalidDataException>(()=>download.AcceptBrowserDownloadAsync(path,"Bad"));
        Assert.Single(Directory.GetFiles(settings.Current.HomebrewDownloadFolder));
    }
    private static byte[] Package() { var bytes=new byte[4096]; bytes[0]=0x7f; bytes[1]=0x43; bytes[2]=0x4e; bytes[3]=0x54; return bytes; }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request,token); }
    private sealed class CancelOnReadStream(byte[] bytes, CancellationTokenSource token) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken=default)
        { var count=await base.ReadAsync(buffer,cancellationToken); token.Cancel(); return count; }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
}
