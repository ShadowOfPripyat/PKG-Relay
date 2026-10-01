using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PkgRelay.Infrastructure;
using PkgRelay.Models;
using PkgRelay.Services;
using System.Net;
using System.Text.Json;
using Windows.Storage.Pickers;
namespace PkgRelay.Views;
public sealed partial class HomebrewPage : Page
{
    private readonly DispatcherQueueTimer _searchTimer;
    private HomebrewItem[] _allItems = [];
    private HomebrewApp[] _apps = [];
    private bool _initializing = true, _downloading;
    private int _view;
    private CancellationTokenSource? _refresh;
    private string? _loadedProvider;
    public ObservableCollection<HomebrewApp> FilteredItems { get; } = [];
    public HomebrewPage()
    {
        InitializeComponent();
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(160);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => ApplyFilter();
        var preferences = App.Services.Settings.Current;
        SortBox.SelectedIndex = Math.Clamp(preferences.HomebrewSortMode, 0, 4);
        CategoryBox.ItemsSource = new[] { "All categories" };
        CategoryBox.SelectedIndex = 0;
        _initializing = false;
        SetView(Math.Clamp(preferences.HomebrewViewMode, 0, 2), false);
        Loaded += HomebrewPage_Loaded;
        Unloaded += (_, _) => { _searchTimer.Stop(); _refresh?.Cancel(); SkeletonMotion.SetActive(SkeletonHost, false); };
    }
    private async void HomebrewPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_allItems.Length == 0 || _loadedProvider != App.Services.Settings.Current.HomebrewApiUrl)
            await RefreshCatalogAsync();
    }
    public async Task RefreshCatalogAsync()
    {
        if (_downloading) return;
        _refresh?.Cancel();
        using var request = new CancellationTokenSource();
        _refresh = request;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        SetLoading(true);
        try
        {
            var provider = App.Services.Settings.Current.HomebrewApiUrl;
            var items = await App.Services.Homebrew.LoadAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            _allItems = items.ToArray();
            _apps = HomebrewCatalog.Group(_allItems);
            _loadedProvider = provider;
            ProviderText.Text = App.Services.Homebrew.ProviderName;
            _initializing = true;
            var categories = new[] { "All categories" }.Concat(_allItems.Select(x => x.Category).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)).ToArray();
            CategoryBox.ItemsSource = categories;
            var preferred = Array.FindIndex(categories, x => x == App.Services.Settings.Current.HomebrewCategoryFilter);
            CategoryBox.SelectedIndex = Math.Max(0, preferred);
            _initializing = false;
            ApplyFilter();
            if (App.Services.Homebrew.Notice.Length > 0) ShowInfo(InfoBarSeverity.Informational, "Catalog available", App.Services.Homebrew.Notice);
            else HomebrewInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Never display entries from another provider under a new URL.
            if (_loadedProvider != App.Services.Settings.Current.HomebrewApiUrl) { _allItems = []; _apps = []; }
            ApplyFilter();
            ShowInfo(InfoBarSeverity.Warning, "Homebrew provider unavailable", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_refresh, request))
            {
                _refresh = null;
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                SetLoading(false);
            }
        }
    }
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_initializing || _searchTimer is null) return;
        _searchTimer.Stop(); _searchTimer.Start();
    }
    private void Options_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_initializing) return;
        ApplyFilter();
        _ = SavePreferencesAsync();
    }
    private void ApplyFilter()
    {
        var category = CategoryBox.SelectedIndex <= 0 ? "" : CategoryBox.SelectedItem as string ?? "";
        var items = HomebrewCatalog.QueryApps(_apps, SearchBox.Text.Trim(), category, Math.Max(0, SortBox.SelectedIndex));
        FilteredItems.Clear();
        foreach (var item in items) FilteredItems.Add(item);
        ResultCountText.Text = $"{items.Length} of {_apps.Length} apps • {_allItems.Length} versions";
        EmptyPanel.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _allItems.Length == 0 ? "No Homebrew available" : "No matching Homebrew";
        EmptyMessage.Text = _allItems.Length == 0 ? "Refresh the catalog or check its URL in Settings → Homebrew." : "Try another search or category.";
    }
    private void View_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out var mode)) SetView(mode, true);
    }
    private void SetView(int mode, bool save)
    {
        _view = mode;
        ListViewButton.IsChecked = mode == 0; GridViewButton.IsChecked = mode == 1; TileViewButton.IsChecked = mode == 2;
        HomebrewList.ItemsSource = mode == 0 ? FilteredItems : null;
        HomebrewGrid.ItemsSource = mode == 0 ? null : FilteredItems;
        HomebrewList.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomebrewGrid.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        HomebrewGrid.ItemTemplate = (DataTemplate)Resources[mode == 2 ? "TileTemplate" : "CardTemplate"];
        HomebrewGrid.ItemsPanel = (ItemsPanelTemplate)Resources[mode == 2 ? "TilePanel" : "WidePanel"];
        SkeletonList.ItemTemplate = (DataTemplate)Resources[mode == 0 ? "RowSkeleton" : "CardSkeleton"];
        SkeletonList.ItemsPanel = (ItemsPanelTemplate)Resources[mode == 0 ? "RowSkeletonPanel" : "WidePanel"];
        if (save) { NativeMotion.Entrance(mode == 0 ? HomebrewList : HomebrewGrid, 12); _ = SavePreferencesAsync(); }
    }
    private async Task SavePreferencesAsync()
    {
        try
        {
            var settings = App.Services.Settings.Current;
            settings.HomebrewViewMode = _view; settings.HomebrewSortMode = Math.Max(0, SortBox.SelectedIndex);
            settings.HomebrewCategoryFilter = CategoryBox.SelectedIndex <= 0 ? "" : CategoryBox.SelectedItem as string ?? "";
            await App.Services.Settings.SaveAsync();
        }
        catch (Exception ex) { ShowInfo(InfoBarSeverity.Warning, "Unable to save catalog preferences", ex.Message); }
    }
    private void CatalogImageFailed(object sender, ExceptionRoutedEventArgs args) { if (sender is Image image) image.Opacity = 0; }
    private void CatalogImageOpened(object sender, RoutedEventArgs args) { if (sender is Image image) image.Opacity = 1; }
    private async void Install_Click(object sender, RoutedEventArgs args) { if ((sender as FrameworkElement)?.Tag is HomebrewApp item) await DownloadAndQueueAsync(item.SelectedVersion, true); }
    private async void Download_Click(object sender, RoutedEventArgs args) { if ((sender as FrameworkElement)?.Tag is HomebrewApp item) await DownloadAndQueueAsync(item.SelectedVersion, false); }
    private async Task DownloadAndQueueAsync(HomebrewItem item, bool start)
    {
        if (_downloading) return;
        _downloading = true;
        HomebrewList.IsEnabled = false;
        HomebrewGrid.IsEnabled = false;
        ShowInfo(InfoBarSeverity.Informational, $"Downloading {item.Name}", "Preparing a local copy…");
        try
        {
            var progress = new Progress<double>(percent => HomebrewInfoBar.Message = $"Downloaded {percent:0.0}%");
            var suggested = string.Join(" - ", new[] { item.Name, item.TitleId, item.Version }.Where(x => x.Length > 0));
            string path;
            try { path = await App.Services.Downloads.DownloadAsync(item.PackageUrl, suggested, progress); }
            catch (Exception ex) when (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden } || ex is InvalidDataException && ex.Message.Contains("webpage"))
            {
                var browserFile = await HomebrewBrowser.DownloadAsync(XamlRoot, App.Services.Settings, new Uri(item.PackageUrl), ".pkg", "Verify and download " + item.Name);
                if (browserFile is null) { ShowInfo(InfoBarSeverity.Informational, "Download canceled", "You can try this download again at any time."); return; }
                try { path = await App.Services.Downloads.AcceptBrowserDownloadAsync(browserFile, suggested); }
                finally { File.Delete(browserFile); }
            }
            await App.Services.Queue.AddLocalAsync(path);
            ShowInfo(InfoBarSeverity.Success, "Added to Packages", start ? $"{item.Name} is ready for the PS4 install queue." : $"{item.Name} was downloaded to this PC and added to Packages.");
            if (start) App.Services.Queue.Start();
        }
        catch (Exception ex) { ShowInfo(InfoBarSeverity.Error, "Homebrew download failed", ex.Message); }
        finally
        {
            _downloading = false;
            HomebrewList.IsEnabled = true;
            HomebrewGrid.IsEnabled = true;
        }
    }
    private void SetLoading(bool loading)
    {
        var show = loading && _apps.Length == 0;
        SkeletonHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SkeletonList.ItemsSource = show ? Enumerable.Range(0, 6).ToArray() : null;
        SkeletonMotion.SetActive(SkeletonHost, show);
        LoadingRing.Visibility = Visibility.Collapsed;
        if (show) { HomebrewList.Visibility = Visibility.Collapsed; HomebrewGrid.Visibility = Visibility.Collapsed; }
        else { HomebrewList.Visibility = _view == 0 ? Visibility.Visible : Visibility.Collapsed; HomebrewGrid.Visibility = _view == 0 ? Visibility.Collapsed : Visibility.Visible; }
    }
    private async void ConnectCatalog_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var path = await HomebrewBrowser.DownloadAsync(XamlRoot, App.Services.Settings, new Uri("https://api.pkg-zone.com/store.db"), ".db", "Connect full Homebrew catalog");
            if (path is null) return;
            try { await App.Services.Homebrew.ImportCatalogAsync(path); }
            finally { File.Delete(path); }
            await UseFullCatalogAsync();
        }
        catch (Exception ex) { ShowInfo(InfoBarSeverity.Warning, "Catalog connection failed", ex.Message + " You can also import a saved store.db or JSON catalog."); }
    }
    private async void ImportCatalog_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".db"); picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await App.Services.Homebrew.ImportCatalogAsync(file.Path);
            await UseFullCatalogAsync();
        }
        catch (Exception ex) { ShowInfo(InfoBarSeverity.Warning, "Catalog import failed", ex.Message); }
    }
    private async Task UseFullCatalogAsync()
    {
        App.Services.Settings.Current.HomebrewApiUrl = "";
        await App.Services.Settings.SaveAsync();
        await RefreshCatalogAsync();
    }
    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        HomebrewInfoBar.Severity = severity; HomebrewInfoBar.Title = title; HomebrewInfoBar.Message = message; HomebrewInfoBar.IsOpen = true;
    }
}
