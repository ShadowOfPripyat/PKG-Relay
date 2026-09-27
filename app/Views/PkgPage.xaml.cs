using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PkgRelay.Models;
using PkgRelay.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PkgRelay.Views;

public sealed partial class PkgPage : Page
{
    public InstallQueueService Queue => App.Services.Queue;
    public ObservableCollection<InstallJob> FilteredJobs { get; } = new();

    private readonly HashSet<InstallJob> _selectedJobs = [];
    private bool _usingFilteredView;
    private bool _persistingNativeReorder;
    private bool _syncingSelectionCheckBoxes;

    public Visibility EmptyQueueVisibility =>
        Queue.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility QueueListVisibility =>
        Queue.Jobs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string QueueSummaryText
    {
        get
        {
            if (Queue.Jobs.Count == 0)
                return "Add packages, verify metadata, then install them in queue order.";

            var localBytes =
                Queue.Jobs
                    .Where(x => x.FileSize > 0)
                    .Sum(x => x.FileSize);

            var sizeText =
                localBytes > 0
                    ? $" • {FormatSize(localBytes)}"
                    : "";

            return $"{Queue.Jobs.Count} package{(Queue.Jobs.Count == 1 ? "" : "s")}{sizeText}";
        }
    }

    private bool _jobsSubscribed;

    public PkgPage()
    {
        InitializeComponent();

        Loaded += PkgPage_Loaded;
        Unloaded += PkgPage_Unloaded;
    }

    private void PkgPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_jobsSubscribed)
        {
            Queue.Jobs.CollectionChanged += Jobs_CollectionChanged;
            Queue.ErrorOccurred += Queue_ErrorOccurred;
            Queue.WarningOccurred += Queue_WarningOccurred;
            _jobsSubscribed = true;
        }

        foreach (var job in Queue.Jobs)
            job.PropertyChanged += Job_PropertyChanged;

        ApplyQueueView();
        ApplyDisplaySettings();
        UpdateSelectionSummary();
    }

    private void PkgPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_jobsSubscribed)
            return;

        Queue.Jobs.CollectionChanged -= Jobs_CollectionChanged;
        Queue.ErrorOccurred -= Queue_ErrorOccurred;
        Queue.WarningOccurred -= Queue_WarningOccurred;

        foreach (var job in Queue.Jobs)
            job.PropertyChanged -= Job_PropertyChanged;

        _jobsSubscribed = false;
    }

    private void Jobs_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (InstallJob job in e.OldItems)
                job.PropertyChanged -= Job_PropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (InstallJob job in e.NewItems)
                job.PropertyChanged += Job_PropertyChanged;
        }

        _selectedJobs.RemoveWhere(
            job => !Queue.Jobs.Contains(job));

        ApplyQueueView();
        UpdateSelectionSummary();

        if (e.Action == NotifyCollectionChangedAction.Move &&
            !_usingFilteredView &&
            !_persistingNativeReorder)
        {
            _ = PersistNativeReorderAsync();
        }
    }

    private void Job_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(InstallJob.Status) or
            nameof(InstallJob.DisplayName) or
            nameof(InstallJob.TitleId) or
            nameof(InstallJob.ContentId))
        {
            ApplyQueueView();
            }

        Bindings.Update();
    }

    private async Task PersistNativeReorderAsync()
    {
        try
        {
            _persistingNativeReorder = true;
            await Queue.PersistOrderAsync();
        }
        catch (Exception ex)
        {
            ShowError(
                "Unable to save queue order",
                ex.Message);
        }
        finally
        {
            _persistingNativeReorder = false;
        }
    }

    private void QueueSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        ClearPackageSelection();
        ApplyQueueView();
        UpdateSelectionSummary();
    }

    private void QueueFilterBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ClearPackageSelection();
        ApplyQueueView();
        UpdateSelectionSummary();
    }

    private void ApplyQueueView()
    {
        if (QueueList is null ||
            QueueSearchBox is null ||
            QueueFilterBox is null)
        {
            return;
        }

        var search =
            QueueSearchBox.Text.Trim();

        var filter =
            (QueueFilterBox.SelectedItem as ComboBoxItem)
                ?.Tag?.ToString()
            ?? "all";

        _usingFilteredView =
            !string.IsNullOrWhiteSpace(search) ||
            !string.Equals(
                filter,
                "all",
                StringComparison.OrdinalIgnoreCase);

        QueueList.CanReorderItems =
            !_usingFilteredView;

        QueueList.CanDragItems =
            !_usingFilteredView;

        if (!_usingFilteredView)
        {
            QueueList.ItemsSource = Queue.Jobs;
            Bindings.Update();
            return;
        }

        var matches = Queue.Jobs
            .Where(job =>
                MatchesFilter(job, filter) &&
                MatchesSearch(job, search))
            .ToArray();

        FilteredJobs.Clear();

        foreach (var job in matches)
            FilteredJobs.Add(job);

        QueueList.ItemsSource = FilteredJobs;
        Bindings.Update();
    }

    private static bool MatchesFilter(
        InstallJob job,
        string filter)
    {
        return filter switch
        {
            "waiting" =>
                job.Status == InstallStatus.Waiting,

            "active" =>
                job.Status is
                    InstallStatus.Preparing or
                    InstallStatus.Sending or
                    InstallStatus.Installing or
                    InstallStatus.Paused or
                    InstallStatus.Recovering or
                    InstallStatus.Finalizing,

            "failed" =>
                job.Status is
                    InstallStatus.Failed or
                    InstallStatus.Cancelled,

            "completed" =>
                job.Status == InstallStatus.Completed,

            _ => true
        };
    }

    private static bool MatchesSearch(
        InstallJob job,
        string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return new[]
        {
            job.DisplayName,
            job.TitleId,
            job.ContentId,
            job.KindText,
            job.PackageIdentity,
            Path.GetFileName(job.FilePath)
        }.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains(
                search,
                StringComparison.CurrentCultureIgnoreCase));
    }

    private InstallJob[] GetSelectedJobs()
    {
        return _selectedJobs
            .Where(Queue.Jobs.Contains)
            .ToArray();
    }

    private void SelectionCheckBox_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.Tag is not InstallJob job)
        {
            return;
        }

        _syncingSelectionCheckBoxes = true;

        try
        {
            checkBox.IsChecked =
                _selectedJobs.Contains(job);
        }
        finally
        {
            _syncingSelectionCheckBoxes = false;
        }
    }

    private void SelectionCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (_syncingSelectionCheckBoxes ||
            sender is not CheckBox checkBox ||
            checkBox.Tag is not InstallJob job)
        {
            return;
        }

        _selectedJobs.Add(job);
        UpdateSelectionSummary();
    }

    private void SelectionCheckBox_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        if (_syncingSelectionCheckBoxes ||
            sender is not CheckBox checkBox ||
            checkBox.Tag is not InstallJob job)
        {
            return;
        }

        _selectedJobs.Remove(job);
        UpdateSelectionSummary();
    }

    private void ClearPackageSelection()
    {
        if (_selectedJobs.Count == 0)
            return;

        _selectedJobs.Clear();

        // Recreate the visible view so virtualized row CheckBoxes are
        // reloaded from the page-local selection state.
        if (QueueList is not null)
        {
            var source = QueueList.ItemsSource;
            QueueList.ItemsSource = null;
            QueueList.ItemsSource = source;
        }
    }

    private void UpdateSelectionSummary()
    {
        if (SelectionSummaryText is null ||
            SelectionActionsPanel is null ||
            QueueList is null)
        {
            return;
        }

        var selected =
            GetSelectedJobs();

        var hasSelection =
            selected.Length > 0;

        SelectionSummaryText.Visibility =
            hasSelection
                ? Visibility.Visible
                : Visibility.Collapsed;

        SelectionActionsPanel.Visibility =
            hasSelection
                ? Visibility.Visible
                : Visibility.Collapsed;

        SelectionSummaryText.Text =
            hasSelection
                ? $"{selected.Length} selected"
                : "";

        RetrySelectedButton.IsEnabled =
            selected.Any(x => x.CanRetry);

        CancelSelectedButton.IsEnabled =
            selected.Any(x => x.CanCancel);

        RemoveSelectedButton.IsEnabled =
            selected.Any(IsRemovable);
    }

    private static bool IsRemovable(
        InstallJob job)
    {
        return job.Status is not
            InstallStatus.Installing and not
            InstallStatus.Paused and not
            InstallStatus.Preparing and not
            InstallStatus.Sending and not
            InstallStatus.Recovering and not
            InstallStatus.Finalizing;
    }

    private async void RetrySelected_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var count =
                await Queue.RetryManyAsync(
                    GetSelectedJobs());

            if (count == 0)
            {
                ShowWarning(
                    "Nothing to retry",
                    "Select one or more failed or cancelled packages.");
            }
        }
        catch (Exception ex)
        {
            ShowError(
                "Unable to retry selection",
                ex.Message);
        }
    }

    private async void CancelSelected_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var count =
                await Queue.CancelManyAsync(
                    GetSelectedJobs());

            if (count == 0)
            {
                ShowWarning(
                    "Nothing to cancel",
                    "Select one or more cancellable packages.");
            }
        }
        catch (Exception ex)
        {
            ShowError(
                "Unable to cancel selection",
                ex.Message);
        }
    }

    private async void RemoveSelected_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var selected =
                GetSelectedJobs();

            var count =
                await Queue.RemoveManyAsync(
                    selected);

            if (count == 0)
            {
                ShowWarning(
                    "Nothing removed",
                    selected.Length == 0
                        ? "Select one or more packages first."
                        : "Active packages must be cancelled before they can be removed.");
            }
        }
        catch (Exception ex)
        {
            ShowError(
                "Unable to remove selection",
                ex.Message);
        }
    }

    private void Queue_ErrorOccurred(
        object? sender,
        QueueErrorEventArgs e)
    {
        ShowError(e.Title, e.Message);
    }

    private void Queue_WarningOccurred(
        object? sender,
        QueueErrorEventArgs e)
    {
        ShowWarning(
            e.Title,
            e.Message);
    }

    private void ApplyDisplaySettings()
    {
        var showBorders =
            App.Services.Settings.Current.ShowPkgListBorders;

        QueueListHost.BorderThickness =
            showBorders
                ? new Thickness(1)
                : new Thickness(0);

        QueueListHost.BorderBrush =
            showBorders
                ? Application.Current.Resources[
                    "DividerStrokeColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush
                : null;

        QueueList.ItemContainerStyle =
            Resources[
                showBorders
                    ? "PkgQueueItemBorderedStyle"
                    : "PkgQueueItemStyle"] as Style;
    }

    public async Task AddPackagesAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads
        };

        picker.FileTypeFilter.Add(".pkg");
        InitializePicker(picker);

        var files = await picker.PickMultipleFilesAsync();

        foreach (var file in files)
            await AddPackageAsync(file.Path);
    }

    private async void AddPkg_Click(
        object sender,
        RoutedEventArgs e)
    {
        await AddPackagesAsync();
    }

    public async Task AddFolderFromPickerAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads
        };

        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);

        var folder = await picker.PickSingleFolderAsync();

        if (folder is null)
            return;

        await AddFolderAsync(folder.Path);
    }

    public async Task StartQueueAsync()
    {
        try
        {
            StatusBar.IsOpen = false;
            await Queue.StartPendingAsync();
        }
        catch (Exception ex)
        {
            ShowError("Queue error", ex.Message);
        }
    }

    public async Task ScanDefaultFolderAsync()
    {
        var folder =
            App.Services.Settings.Current.PackageFolder;

        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowWarning(
                "Default PKG folder is not configured",
                "Choose a default PKG folder in Settings → Package server first.");
            return;
        }

        if (!Directory.Exists(folder))
        {
            ShowError(
                "Default PKG folder is unavailable",
                $"Folder not found: {folder}");
            return;
        }

        await AddFolderAsync(folder);
    }

    public async Task RetryFailedAsync()
    {
        try
        {
            var count =
                await Queue.RetryAllFailedAsync();

            if (count == 0)
            {
                ShowWarning(
                    "No failed packages",
                    "There are no failed queue items to retry.");
            }
        }
        catch (Exception ex)
        {
            ShowError(
                "Unable to retry failed packages",
                ex.Message);
        }
    }

    public async Task ClearFinishedAsync()
    {
        try
        {
            await Queue.ClearFinishedAsync();
        }
        catch (Exception ex)
        {
            ShowError("Unable to clear queue", ex.Message);
        }
    }

    public async Task TestPs4Async()
    {
        var connected = await App.Services.Rpi.TestConnectionAsync();

        StatusBar.Severity =
            connected
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;

        StatusBar.Title =
            connected
                ? "PS4 reachable"
                : "PS4 unavailable";

        StatusBar.Message =
            connected
                ? "The RPI TCP port is reachable."
                : "Check the PS4 address, port, network and Remote Package Installer.";

        StatusBar.IsOpen = true;
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        e.Handled = true;

        var items = await e.DataView.GetStorageItemsAsync();

        foreach (var item in items)
        {
            switch (item)
            {
                case StorageFile file
                    when file.FileType.Equals(
                        ".pkg",
                        StringComparison.OrdinalIgnoreCase):

                    await AddPackageAsync(file.Path);
                    break;

                case StorageFolder folder:
                    await AddFolderAsync(folder.Path);
                    break;
            }
        }
    }

    private async Task AddFolderAsync(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = App.Services.Settings.Current.RecursiveScan,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        try
        {
            foreach (var package in Directory.EnumerateFiles(root, "*.pkg", options))
                await AddPackageAsync(package);
        }
        catch (Exception ex)
        {
            ShowError("Folder scan failed", ex.Message);
        }
    }

    private async Task AddPackageAsync(string path)
    {
        try
        {
            await Queue.AddLocalAsync(path);
        }
        catch (Exception ex)
        {
            ShowError(
                $"Unable to add {Path.GetFileName(path)}",
                ex.Message);
        }
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallJob job)
            return;

        try
        {
            await Queue.MoveUpAsync(job);
        }
        catch (Exception ex)
        {
            ShowError("Unable to reorder package", ex.Message);
        }
    }

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallJob job)
            return;

        try
        {
            await Queue.MoveDownAsync(job);
        }
        catch (Exception ex)
        {
            ShowError("Unable to reorder package", ex.Message);
        }
    }

    private async void MoveTop_Click(
        object sender,
        RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallJob job)
            return;

        try
        {
            await Queue.MoveTopAsync(job);
        }
        catch (Exception ex)
        {
            ShowError("Unable to reorder package", ex.Message);
        }
    }

    private async void MoveBottom_Click(
        object sender,
        RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallJob job)
            return;

        try
        {
            await Queue.MoveBottomAsync(job);
        }
        catch (Exception ex)
        {
            ShowError("Unable to reorder package", ex.Message);
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallJob job)
            return;

        try
        {
            await Queue.PauseAsync(job);
        }
        catch (Exception ex)
        {
            ShowError("Unable to pause package", ex.Message);
        }
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallJob job)
            return;

        try
        {
            await Queue.ResumeAsync(job);
        }
        catch (Exception ex)
        {
            ShowError("Unable to resume package", ex.Message);
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallJob job)
            return;

        try
        {
            await Queue.CancelAsync(job);
        }
        catch (Exception ex)
        {
            ShowError("Unable to cancel package", ex.Message);
        }
    }

    private async void Retry_Click(
        object sender,
        RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallJob job)
            return;

        try
        {
            await Queue.RetryAsync(job);
        }
        catch (Exception ex)
        {
            ShowError("Unable to retry package", ex.Message);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstallJob job)
            return;

        try
        {
            await Queue.RemoveAsync(job);
        }
        catch (Exception ex)
        {
            ShowError("Unable to remove job", ex.Message);
        }
    }

    private void ShowError(string title, string message)
    {
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void ShowWarning(
        string title,
        string message)
    {
        StatusBar.Severity =
            InfoBarSeverity.Warning;

        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var index = 0;

        while (value >= 1024 &&
               index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }

    private static void InitializePicker(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
