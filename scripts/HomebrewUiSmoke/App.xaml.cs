using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PkgRelay.Infrastructure;
using PkgRelay.Services;
using PkgRelay.Views;
using Microsoft.Data.Sqlite;
using System.Net.Sockets;

namespace PkgRelay;
public partial class App : Application
{
    public static SmokeServices Services { get; private set; } = null!;
    private Window? window;
    public static Window MainWindow => ((App)Current).window!;
    private static string Output => Path.Combine(AppContext.BaseDirectory, "smoke.log");
    private static void Log(string message) => File.AppendAllText(Output, message + Environment.NewLine);
    public App()
    {
        InitializeComponent();
        File.WriteAllText(Output, "Starting native Homebrew smoke check\n");
        UnhandledException += (_, args) => Log("UNHANDLED: " + args.Exception);
        AppDomain.CurrentDomain.FirstChanceException += (_, args) => { if (args.Exception is ArgumentException or System.Runtime.InteropServices.COMException) Log("FIRST CHANCE: " + args.Exception); };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = new SmokeServices();
        window = new Window { Title = "Homebrew UI smoke check" };
        // Keep the test off screen and avoid activation, but allow WinUI to
        // load templates and dispatch the actual control events.
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 1100, 800));
        window.AppWindow.Show(false);
        window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await CheckStatusBadgesAsync();
                var converter = new HomebrewImageConverter();
                foreach (var image in new object?[] { null, "", "  ", "not a URI", "file:///C:/secret.png", "javascript:alert(1)" })
                    if (converter.Convert(image!, typeof(ImageSource), null!, "en") is not null) throw new InvalidOperationException("Invalid artwork must retain the fallback icon.");
                if (converter.Convert("https://catalog.test/icon.png", typeof(ImageSource), null!, "en") is not BitmapImage bitmap || bitmap.UriSource.Host != "catalog.test") throw new InvalidOperationException("Valid artwork was rejected.");
                Log("Artwork converter: missing, empty, invalid, unsafe and valid URLs passed");
                Log("Constructing page");
                var page = new HomebrewPage();
                window.Content = page;
                Log("Page attached");
                var refresh = page.RefreshCatalogAsync();
                Layout(page);
                if (((Grid)page.FindName("SkeletonHost")).Visibility != Visibility.Visible) throw new InvalidOperationException("Initial catalog load did not show skeleton placeholders.");
                await refresh;
                await Task.Delay(800);
                Layout(page);
                Log("Catalog rendered: " + page.FilteredItems.Count);
                if (!page.IsLoaded) throw new InvalidOperationException("The native page did not load.");
                if (page.FilteredItems.Count != 2) throw new InvalidOperationException("Catalog failed to load.");
                var app = page.FilteredItems.First(x => x.Name.Contains("utility"));
                if (app.Versions.Count != 2 || app.SelectedVersion.Version != "2.10") throw new InvalidOperationException("Latest version was not selected.");
                var list = (ListView)page.FindName("HomebrewList");
                var versionBox = FindVersionBox(list.ContainerFromItem(app));
                if (versionBox is null) throw new InvalidOperationException("The version selector did not render.");
                versionBox.SelectedItem = app.Versions[1];
                if (app.SelectedVersion.Version != "2.9") throw new InvalidOperationException("The version dropdown did not select its actual package.");
                versionBox.SelectedItem = app.Versions[0];
                Log("Version dropdown selected and restored the correct package");
                var search = (AutoSuggestBox)page.FindName("SearchBox");
                search.ApplyTemplate();
                search.Text = "manager";
                await Until(() => page.FilteredItems.Count == 1, "Search did not filter the catalog.");
                search.Text = "";
                await Until(() => page.FilteredItems.Count == 2, "Clearing search did not restore the catalog.");
                var category = (ComboBox)page.FindName("CategoryBox");
                category.SelectedIndex = 1;
                await Task.Delay(100);
                if (page.FilteredItems.Count != 1) throw new InvalidOperationException("Category filtering failed.");
                category.SelectedIndex = 0;
                ((ComboBox)page.FindName("SortBox")).SelectedIndex = 1;
                Log("Search, category and sorting passed");
                var setView = typeof(HomebrewPage).GetMethod("SetView", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (var mode in new[] { 1, 2, 0, 2, 1, 0 })
                {
                    setView.Invoke(page, [mode, false]);
                    Layout(page);
                    await Task.Delay(200);
                    Log("View rendered: " + mode);
                }
                window.Content = new Grid();
                await Task.Delay(150);
                window.Content = page;
                await Task.Delay(200);
                var info = (InfoBar)page.FindName("HomebrewInfoBar");
                info.Title = "Smoke notification";
                info.IsOpen = true;
                await Task.Delay(300);
                info.Severity = InfoBarSeverity.Success;
                await Task.Delay(300);
                info.IsOpen = false;
                await Until(() => !info.IsOpen, "InfoBar did not finish closing.");
                await CheckBrowserDownloadAsync(page);
                var manager = new FpkgiDatabaseTool();
                window.Content = manager;
                await Task.Delay(100);
                await manager.DownloadSelectedAsync();
                if ((await Services.FpkgiDatabases.InspectAsync(FpkgiDatabaseKind.Games)).Entries != 1 || (await Services.FpkgiDatabases.InspectAsync(FpkgiDatabaseKind.Dlc)).Entries != 1)
                    throw new InvalidOperationException("The native database tool did not download both defaults.");
                if (((InfoBar)manager.FindName("DatabaseInfoBar")).Severity != InfoBarSeverity.Success || !((TextBlock)manager.FindName("GamesStatus")).Text.Contains("1 entries"))
                    throw new InvalidOperationException("Database status did not update after download.");
                Log("Native FPKGi manager: default selections, downloads, validation and status passed");
                Log("PASS: navigation, catalog load, all views, search/filter/sort, unload/reload, InfoBar state and close");
            }
            catch (Exception ex) { Log("FAIL: " + ex); Environment.ExitCode = 1; }
            finally { window.Close(); Exit(); }
        });
    }
    private static void Layout(HomebrewPage page)
    {
        page.Measure(new Windows.Foundation.Size(1100, 800));
        page.Arrange(new Windows.Foundation.Rect(0, 0, 1100, 800));
        page.UpdateLayout();
    }
    private static async Task Until(Func<bool> condition, string error)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new InvalidOperationException(error);
            await Task.Delay(50);
        }
    }
    private static ComboBox? FindVersionBox(DependencyObject? parent)
    {
        if (parent is null) return null;
        if (parent is ComboBox box) return box;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVersionBox(VisualTreeHelper.GetChild(parent, i)) is { } result) return result;
        return null;
    }
    private static async Task CheckBrowserDownloadAsync(HomebrewPage page)
    {
        Log("Checking browser runtime");
        var fallback = HomebrewBrowser.ManualDownloadContent(new Uri("https://catalog.test/store.db"), ".db", "Runtime missing.");
        if (fallback.Children.OfType<HyperlinkButton>().Count() != 2 || !fallback.Children.OfType<TextBlock>().Single().Text.Contains("Import catalog")) throw new InvalidOperationException("The missing-runtime recovery instructions are incomplete.");
        if (!HomebrewBrowser.IsRuntimeAvailable()) { Log("Missing WebView2 Runtime: external browser/import recovery passed; embedded download skipped"); return; }
        var path = Path.Combine(Services.Settings.DataDirectory, "browser-store.db");
        if (File.Exists(path)) File.Delete(path);
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE homebrews(id TEXT,name TEXT,url TEXT,version TEXT); INSERT INTO homebrews VALUES('TEST00001','Browser test app','https://catalog.test/browser.pkg','1.0');";
            command.ExecuteNonQuery();
        }
        var data = await File.ReadAllBytesAsync(path);
        var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await server.AcceptTcpClientAsync(stop.Token);
                    try
                    {
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                        while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=store.db\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(headers, stop.Token); await stream.WriteAsync(data, stop.Token);
                    }
                    catch (IOException) { /* WebView2 can restart navigation as a download. */ }
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            var port = ((IPEndPoint)server.LocalEndpoint).Port;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var downloaded = await HomebrewBrowser.DownloadAsync(page.XamlRoot, Services.Settings, new Uri($"http://127.0.0.1:{port}/store.db"), ".db", "Browser download smoke check", deadline.Token);
            if (downloaded is null || await Services.Homebrew.ImportCatalogAsync(downloaded) != 1) throw new InvalidOperationException("Browser download did not import its catalog.");
            File.Delete(downloaded);
            Log("Native browser download and database import passed");
        }
        finally { stop.Cancel(); server.Stop(); await serving; }
    }
}
public sealed class SmokeServices
{
    public SettingsService Settings { get; } = new(Path.Combine(AppContext.BaseDirectory, "smoke-data"));
    public HomebrewService Homebrew { get; }
    public DownloadService Downloads { get; }
    public FpkgiDatabaseService FpkgiDatabases { get; }
    public SmokeQueue Queue { get; } = new();
    public SmokeServices()
    {
        Settings.Current.HomebrewApiUrl = "https://catalog.test/apps.json";
        Homebrew = new HomebrewService(Settings, new CatalogHandler());
        Downloads = new DownloadService(Settings, new LogService(Settings));
        FpkgiDatabases = new FpkgiDatabaseService(Settings, new DatabaseHandler());
    }
}
public sealed class SmokeQueue
{
    public Task AddLocalAsync(string path) => throw new InvalidOperationException("Smoke check never queues packages.");
    public void Start() => throw new InvalidOperationException("Smoke check never contacts a PS4.");
}
internal sealed class CatalogHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        await Task.Delay(300, token);
        return new(HttpStatusCode.OK) { Content = new StringContent("""
            [{"name":"UI test utility","package_url":"https://catalog.test/app.pkg","category":"Utilities","version":"2.10","min_fw":"9.00+"},
             {"name":"UI test utility","package_url":"https://catalog.test/old.pkg","category":"Utilities","version":"2.9","min_fw":"5.05+"},
             {"name":"UI test manager","package_url":"https://catalog.test/manager.pkg","category":"File managers","size":5242880}]
            """, Encoding.UTF8, "application/json") };
    }
}
internal sealed class DatabaseHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri?.AbsoluteUri is not (FpkgiDatabaseService.DefaultGamesUrl or FpkgiDatabaseService.DefaultDlcUrl)) throw new InvalidOperationException("Unexpected database source.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"DATA\":{\"https://catalog.test/test.pkg\":{\"name\":\"UI database test\"}}}", Encoding.UTF8, "application/json") });
    }
}
