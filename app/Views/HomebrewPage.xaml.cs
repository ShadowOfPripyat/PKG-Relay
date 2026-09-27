using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PkgRelay.Models;

namespace PkgRelay.Views;

public sealed partial class HomebrewPage : Page
{
    private IReadOnlyList<HomebrewItem> _catalog = [];

    public ObservableCollection<HomebrewItem> VisibleItems { get; } = new();

    private bool _loadedOnce;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _searchTimer;

    public HomebrewPage()
    {
        InitializeComponent();

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(180);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += SearchTimer_Tick;

        Loaded += HomebrewPage_Loaded;
    }

    private async void HomebrewPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;

        _loadedOnce = true;
        await RefreshAsync();
    }

    public Task RefreshCatalogAsync()
    {
        return RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        LoadingRing.IsActive = true;
        StatusBar.IsOpen = false;

        try
        {
            _catalog = await App.Services.Homebrew.GetCatalogAsync();
            ApplyFilter();

            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Title = "Catalog loaded";
            StatusBar.Message = $"{_catalog.Count} item(s).";
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "Unable to load Homebrew";
            StatusBar.Message =
                $"{ex.Message} Check the Homebrew API URL in Settings.";
            StatusBar.IsOpen = true;
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void SearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        // Filtering a large catalog on every key event causes avoidable UI
        // churn. Debounce briefly so typing stays responsive.
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();

        var filtered =
            string.IsNullOrWhiteSpace(query)
                ? _catalog
                : _catalog.Where(x =>
                    x.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    x.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    x.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    x.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    x.TitleId.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

        VisibleItems.Clear();

        foreach (var item in filtered)
            VisibleItems.Add(item);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HomebrewItem item)
            return;

        await DownloadAndQueueAsync(item, startQueue: true);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HomebrewItem item)
            return;

        await DownloadAndQueueAsync(item, startQueue: false);
    }

    private async Task DownloadAndQueueAsync(
        HomebrewItem item,
        bool startQueue)
    {
        LoadingRing.IsActive = true;
        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = startQueue
            ? $"Preparing {item.Title}"
            : $"Downloading {item.Title}";
        StatusBar.Message = "Starting download…";

        try
        {
            var progress = new Progress<double>(value =>
            {
                StatusBar.Message = $"Downloaded {value:0.0}%";
            });

            var path =
                await App.Services.Downloads.DownloadAsync(
                    item.PackageUrl,
                    BuildFileName(item),
                    progress);

            await App.Services.Queue.AddLocalAsync(path);

            if (startQueue)
            {
                StatusBar.Title = "Added to queue";
                StatusBar.Message = "Starting the pending PKG queue.";

                await App.Services.Queue.StartPendingAsync();
            }
            else
            {
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Title = "Download complete";
                StatusBar.Message =
                    $"{Path.GetFileName(path)} was added to the PKG queue.";
            }
        }
        catch (Exception ex)
        {
            ShowError(
                startQueue ? "Homebrew install failed" : "Download failed",
                ex.Message);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private static string BuildFileName(HomebrewItem item)
    {
        var parts = new[]
        {
            item.Title,
            item.TitleId,
            item.Version
        }.Where(x => !string.IsNullOrWhiteSpace(x));

        return string.Join(" - ", parts);
    }

    private void ShowError(string title, string message)
    {
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
