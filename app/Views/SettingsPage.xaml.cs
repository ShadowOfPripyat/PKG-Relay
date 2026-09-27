using System.Diagnostics;
using System.Net;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PkgRelay.Models;
using Windows.Storage.Pickers;

namespace PkgRelay.Views;

public sealed partial class SettingsPage : Page
{
    private readonly DispatcherQueueTimer _saveTimer;
    private bool _loadingSettings;

    public SettingsPage()
    {
        InitializeComponent();

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += SaveTimer_Tick;

        LoadSettings();

        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (SettingsIndex.SelectedItem is null)
            SettingsIndex.SelectedItem = PlayStationNavItem;
    }

    private void LoadSettings()
    {
        _loadingSettings = true;

        try
        {
            var settings = App.Services.Settings.Current;

            Ps4AddressBox.Text = settings.Ps4Address;
            RpiPortBox.Value = settings.RpiPort;
            LocalAddressBox.Text = settings.LocalAddress;
            HttpPortBox.Value = settings.HttpPort;
            PackageFolderBox.Text = settings.PackageFolder;
            ShowPkgListBordersToggle.IsOn =
                settings.ShowPkgListBorders;
            TimeoutBox.Value = settings.RequestTimeoutMs;
            PollIntervalBox.Value = settings.PollIntervalMs;
            ProgressFailuresBox.Value =
                settings.MaxProgressFailures;
            RecursiveScanToggle.IsOn =
                settings.RecursiveScan;
            WatchDefaultPkgFolderToggle.IsOn =
                settings.WatchDefaultPkgFolder;
            AutoStartPackagesToggle.IsOn =
                settings.AutoStartPackages;
            AutoDiscoverPs4Toggle.IsOn =
                settings.AutoDiscoverPs4OnConnectionFailure;
            ShowHomebrewTabToggle.IsOn =
                settings.ShowHomebrewTab;
            HomebrewApiBox.Text =
                settings.HomebrewApiUrl;
            HomebrewFolderBox.Text =
                settings.HomebrewDownloadFolder;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SettingsIndex_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        var tag =
            args.SelectedItemContainer?.Tag as string
            ?? "playstation";

        PlayStationPanel.Visibility =
            tag == "playstation"
                ? Visibility.Visible
                : Visibility.Collapsed;

        PackageServerPanel.Visibility =
            tag == "server"
                ? Visibility.Visible
                : Visibility.Collapsed;

        RpiBehaviorPanel.Visibility =
            tag == "rpi"
                ? Visibility.Visible
                : Visibility.Collapsed;

        GeneralPanel.Visibility =
            tag == "general"
                ? Visibility.Visible
                : Visibility.Collapsed;

        HomebrewPanel.Visibility =
            tag == "homebrew"
                ? Visibility.Visible
                : Visibility.Collapsed;

        DiagnosticsPanel.Visibility =
            tag == "diagnostics"
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void SettingsCategory_PointerWheelChanged(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var properties =
            e.GetCurrentPoint(
                sender as UIElement)
             .Properties;

        var delta =
            properties.MouseWheelDelta;

        if (delta == 0)
            return;

        var items = new NavigationViewItem[]
        {
            PlayStationNavItem,
            PackageServerNavItem,
            RpiBehaviorNavItem,
            GeneralNavItem,
            HomebrewNavItem,
            DiagnosticsNavItem
        };

        var selected =
            SettingsIndex.SelectedItem as NavigationViewItem
            ?? PlayStationNavItem;

        var index =
            Array.IndexOf(items, selected);

        if (index < 0)
            index = 0;

        var next =
            delta < 0
                ? Math.Min(
                    index + 1,
                    items.Length - 1)
                : Math.Max(
                    index - 1,
                    0);

        if (next == index)
            return;

        SettingsIndex.SelectedItem =
            items[next];

        e.Handled = true;
    }

    private void TextSettingChanged(
        object sender,
        TextChangedEventArgs e)
    {
        ScheduleSave();
    }

    private void NumberSettingChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        ScheduleSave();
    }

    private void ToggleSettingChanged(
        object sender,
        RoutedEventArgs e)
    {
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (_loadingSettings)
            return;

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void SaveTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        await SaveNowAsync();
    }

    private async Task<bool> SaveNowAsync()
    {
        if (!Validate(out var error))
        {
            Show(
                InfoBarSeverity.Error,
                "Invalid setting",
                error);

            return false;
        }

        var settings = App.Services.Settings.Current;

        settings.Ps4Address =
            Ps4AddressBox.Text.Trim();
        settings.RpiPort =
            ToInt(RpiPortBox.Value, 12800);
        settings.LocalAddress =
            LocalAddressBox.Text.Trim();
        settings.HttpPort =
            ToInt(HttpPortBox.Value, 8080);
        settings.PackageFolder =
            PackageFolderBox.Text.Trim();
        settings.ShowPkgListBorders =
            ShowPkgListBordersToggle.IsOn;
        settings.RequestTimeoutMs =
            ToInt(TimeoutBox.Value, 5000);
        settings.PollIntervalMs =
            ToInt(PollIntervalBox.Value, 2500);
        settings.MaxProgressFailures =
            ToInt(ProgressFailuresBox.Value, 8);
        settings.RecursiveScan =
            RecursiveScanToggle.IsOn;
        settings.WatchDefaultPkgFolder =
            WatchDefaultPkgFolderToggle.IsOn;
        settings.AutoStartPackages =
            AutoStartPackagesToggle.IsOn;
        settings.AutoDiscoverPs4OnConnectionFailure =
            AutoDiscoverPs4Toggle.IsOn;
        settings.ShowHomebrewTab =
            ShowHomebrewTabToggle.IsOn;
        settings.HomebrewApiUrl =
            HomebrewApiBox.Text.Trim();
        settings.HomebrewDownloadFolder =
            HomebrewFolderBox.Text.Trim();

        try
        {
            await App.Services.Settings.SaveAsync();

            if (App.MainWindow is MainWindow window)
                window.ApplySettings();

            App.Services.LibraryWatcher.ApplySettings();

            if (ResultBar.IsOpen &&
                ResultBar.Severity ==
                InfoBarSeverity.Error)
            {
                ResultBar.IsOpen = false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Show(
                InfoBarSeverity.Error,
                "Unable to save settings",
                ex.Message);

            return false;
        }
    }

    private async void BrowsePackageFolder_Click(
        object sender,
        RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation =
                PickerLocationId.Downloads
        };

        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);

        var folder =
            await picker.PickSingleFolderAsync();

        if (folder is null)
            return;

        PackageFolderBox.Text = folder.Path;
        await SaveNowAsync();
    }

    public async Task FindPs4Async()
    {
        var port = ToInt(
            RpiPortBox.Value,
            12800);

        Show(
            InfoBarSeverity.Informational,
            "Searching local network",
            $"Looking for Remote Package Installer on TCP port {port}…");

        try
        {
            var hosts =
                await App.Services.Network.FindRpiHostsAsync(
                    port);

            if (hosts.Count == 0)
            {
                Show(
                    InfoBarSeverity.Warning,
                    "No PS4 found",
                    "Make sure Remote Package Installer is running and the PC and PS4 are on the same LAN.");

                return;
            }

            Ps4AddressBox.Text =
                hosts[0].ToString();

            await SaveNowAsync();

            Show(
                hosts.Count == 1
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                hosts.Count == 1
                    ? "PS4 found"
                    : "Multiple RPI hosts found",
                hosts.Count == 1
                    ? $"Found RPI at {hosts[0]}."
                    : $"Found {hosts.Count} hosts. Selected {hosts[0]}; verify it is the correct console.");
        }
        catch (Exception ex)
        {
            Show(
                InfoBarSeverity.Error,
                "Network discovery failed",
                ex.Message);
        }
    }

    public async Task TestPs4Async()
    {
        if (!await SaveNowAsync())
            return;

        var connected =
            await App.Services.Rpi.TestConnectionAsync();

        Show(
            connected
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error,
            connected
                ? "PS4 reachable"
                : "PS4 unavailable",
            connected
                ? "The configured RPI TCP port is reachable."
                : "Check the PS4 address, port, network and Remote Package Installer.");
    }

    private async void RunDiagnostics_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!await SaveNowAsync())
            return;

        DiagnosticsProgressRing.Visibility =
            Visibility.Visible;
        DiagnosticsProgressRing.IsActive = true;
        DiagnosticsResultsPanel.Children.Clear();
        DiagnosticsResultBar.IsOpen = false;

        try
        {
            var report =
                await App.Services.Diagnostics.RunAsync();

            foreach (var item in report.Items)
            {
                var prefix = item.Severity switch
                {
                    DiagnosticSeverity.Success => "✓",
                    DiagnosticSeverity.Warning => "⚠",
                    DiagnosticSeverity.Error => "✕",
                    _ => "•"
                };

                DiagnosticsResultsPanel.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"{prefix} {item.Name}\n{item.Detail}",
                        TextWrapping =
                            TextWrapping.Wrap,
                        MaxWidth = 720
                    });
            }

            DiagnosticsResultBar.Severity =
                report.HasErrors
                    ? InfoBarSeverity.Error
                    : report.HasWarnings
                        ? InfoBarSeverity.Warning
                        : InfoBarSeverity.Success;

            DiagnosticsResultBar.Title =
                report.HasErrors
                    ? "Diagnostics found a blocking issue"
                    : report.HasWarnings
                        ? "Diagnostics completed with warnings"
                        : "Diagnostics passed";

            DiagnosticsResultBar.Message =
                report.HasErrors
                    ? "Resolve the failed checks before starting another transfer."
                    : "The PC-side sender configuration is usable.";

            DiagnosticsResultBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            DiagnosticsResultBar.Severity =
                InfoBarSeverity.Error;
            DiagnosticsResultBar.Title =
                "Diagnostics failed";
            DiagnosticsResultBar.Message =
                ex.Message;
            DiagnosticsResultBar.IsOpen = true;
        }
        finally
        {
            DiagnosticsProgressRing.IsActive = false;
            DiagnosticsProgressRing.Visibility =
                Visibility.Collapsed;
        }
    }

    public void OpenLogs()
    {
        Directory.CreateDirectory(
            App.Services.Log.LogDirectory);

        OpenFolder(
            App.Services.Log.LogDirectory);
    }

    public void OpenData()
    {
        Directory.CreateDirectory(
            App.Services.Settings.DataDirectory);

        OpenFolder(
            App.Services.Settings.DataDirectory);
    }

    private bool Validate(out string error)
    {
        error = "";

        var ps4 = Ps4AddressBox.Text.Trim();

        if (!string.IsNullOrWhiteSpace(ps4) &&
            (!IPAddress.TryParse(
                 ps4,
                 out var ps4Ip) ||
             ps4Ip.AddressFamily !=
             System.Net.Sockets.AddressFamily.InterNetwork))
        {
            error =
                "PS4 address must be a valid IPv4 address.";

            return false;
        }

        var local =
            LocalAddressBox.Text.Trim();

        if (!string.IsNullOrWhiteSpace(local) &&
            (!IPAddress.TryParse(
                 local,
                 out var localIp) ||
             localIp.AddressFamily !=
             System.Net.Sockets.AddressFamily.InterNetwork))
        {
            error =
                "Local address must be empty or a valid IPv4 address.";

            return false;
        }

        var packageFolder =
            PackageFolderBox.Text.Trim();

        if (!string.IsNullOrWhiteSpace(packageFolder) &&
            !Path.IsPathFullyQualified(packageFolder))
        {
            error =
                "Default PKG folder must be empty or an absolute path.";

            return false;
        }

        var homebrew =
            HomebrewApiBox.Text.Trim();

        if (!string.IsNullOrWhiteSpace(homebrew) &&
            !Uri.TryCreate(
                homebrew,
                UriKind.Absolute,
                out _))
        {
            error =
                "Homebrew API must be an absolute URL.";

            return false;
        }

        return true;
    }

    private static int ToInt(
        double value,
        int fallback)
    {
        if (double.IsNaN(value) ||
            value <= 0)
        {
            return fallback;
        }

        return checked((int)value);
    }

    private static void OpenFolder(string path)
    {
        Process.Start(
            new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
    }

    private void Show(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    private static void InitializePicker(
        object picker)
    {
        var hwnd =
            WinRT.Interop.WindowNative.GetWindowHandle(
                App.MainWindow);

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            hwnd);
    }
}
