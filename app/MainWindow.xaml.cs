using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PkgRelay.Views;

namespace PkgRelay;

public sealed partial class MainWindow : Window
{
    private bool _closing;
    private bool _initialNavigationCompleted;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SectionSelector.IsEnabled = false;
        ShellCommandBar.IsEnabled = false;
        Closed += MainWindow_Closed;
    }

    public void InitializeAfterActivation()
    {
        if (_initialNavigationCompleted)
            return;

        _initialNavigationCompleted = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                SectionSelector.SelectedItem = PkgSelectorItem;
                NavigateTo("pkg");
            }
            catch (Exception ex)
            {
                ShowNavigationFailure("PKG", ex);
            }
        });
    }

    public void ApplySettings()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplySettings);
            return;
        }

        var showHomebrew =
            App.Services.Settings.Current.ShowHomebrewTab;

        HomebrewSelectorItem.Visibility =
            showHomebrew
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (!showHomebrew &&
            ReferenceEquals(
                SectionSelector.SelectedItem,
                HomebrewSelectorItem))
        {
            SectionSelector.SelectedItem = PkgSelectorItem;
            NavigateTo("pkg");
        }
    }

    public void SetAppReady(bool ready)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetAppReady(ready));
            return;
        }

        SectionSelector.IsEnabled = ready;
        ShellCommandBar.IsEnabled = ready;

        Title = ready
            ? "PKG Relay"
            : "PKG Relay — Starting…";
    }

    public void SetStartupWarning(string message)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(
                () => SetStartupWarning(message));
            return;
        }

        SectionSelector.IsEnabled = true;
        ShellCommandBar.IsEnabled = true;
        Title = "PKG Relay — Startup warning";

        ShowShellInfo(
            InfoBarSeverity.Warning,
            "Startup warning",
            message);
    }

    private void SectionSelector_SelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tag)
            return;

        NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        var page = tag switch
        {
            "pkg" => typeof(PkgPage),
            "homebrew" => typeof(HomebrewPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(PkgPage)
        };

        try
        {
            ShellInfoBar.IsOpen = false;

            if (ContentFrame.CurrentSourcePageType != page &&
                !ContentFrame.Navigate(
                    page,
                    null,
                    new EntranceNavigationTransitionInfo()))
            {
                throw new InvalidOperationException(
                    $"Navigation to {page.Name} returned false.");
            }

            ConfigureShellCommandBar(tag);
        }
        catch (Exception ex)
        {
            ShowNavigationFailure(page.Name, ex);
        }
    }

    private void ConfigureShellCommandBar(string tag)
    {
        ShellCommandBar.PrimaryCommands.Clear();
        ShellCommandBar.SecondaryCommands.Clear();

        switch (tag)
        {
            case "pkg":
                AddPrimaryCommand(
                    "Add PKG",
                    "Add one or more PKG files",
                    new SymbolIcon(Symbol.Add),
                    PkgAdd_Click);

                AddPrimaryCommand(
                    "Add folder",
                    "Add PKG files from a folder",
                    CreateFluentIcon("\uE8B7"),
                    PkgAddFolder_Click);

                ShellCommandBar.PrimaryCommands.Add(
                    new AppBarSeparator());

                AddPrimaryCommand(
                    "Start queue",
                    "Start installing waiting packages",
                    new SymbolIcon(Symbol.Play),
                    PkgStartQueue_Click);

                AddSecondaryCommand(
                    "Test PS4",
                    "Test the Remote Package Installer connection",
                    PkgTestPs4_Click);

                AddSecondaryCommand(
                    "Scan default folder",
                    "Scan the configured default PKG folder",
                    PkgScanDefaultFolder_Click);

                AddSecondaryCommand(
                    "Retry failed",
                    "Reset failed packages to Waiting",
                    PkgRetryFailed_Click);

                AddSecondaryCommand(
                    "Clear finished",
                    "Remove completed, cancelled and failed queue items",
                    PkgClearFinished_Click);
                break;

            case "homebrew":
                AddPrimaryCommand(
                    "Refresh",
                    "Reload the Homebrew catalog",
                    new SymbolIcon(Symbol.Refresh),
                    HomebrewRefresh_Click);
                break;

            case "settings":
                AddPrimaryCommand(
                    "Find PS4",
                    "Search the local network for Remote Package Installer",
                    new SymbolIcon(Symbol.Find),
                    SettingsFindPs4_Click);

                AddPrimaryCommand(
                    "Test PS4",
                    "Test the configured PS4 connection",
                    CreateFluentIcon("\uE774"),
                    SettingsTestPs4_Click);

                AddSecondaryCommand(
                    "Open logs",
                    "Open the application log folder",
                    SettingsOpenLogs_Click);

                AddSecondaryCommand(
                    "Open app data",
                    "Open the application data folder",
                    SettingsOpenData_Click);
                break;
        }
    }

    private void AddPrimaryCommand(
        string label,
        string tooltip,
        IconElement icon,
        RoutedEventHandler handler)
    {
        var button =
            CreateCommand(
                label,
                tooltip,
                handler);

        button.Icon = icon;
        ShellCommandBar.PrimaryCommands.Add(button);
    }

    private void AddSecondaryCommand(
        string label,
        string tooltip,
        RoutedEventHandler handler)
    {
        ShellCommandBar.SecondaryCommands.Add(
            CreateCommand(
                label,
                tooltip,
                handler));
    }

    private static AppBarButton CreateCommand(
        string label,
        string tooltip,
        RoutedEventHandler handler)
    {
        var button = new AppBarButton
        {
            Label = label
        };

        ToolTipService.SetToolTip(
            button,
            tooltip);

        button.Click += handler;
        return button;
    }

    private static FontIcon CreateFluentIcon(string glyph)
    {
        return new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = glyph
        };
    }

    private async void PkgAdd_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is PkgPage page)
            await page.AddPackagesAsync();
    }

    private async void PkgAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is PkgPage page)
            await page.AddFolderFromPickerAsync();
    }

    private async void PkgStartQueue_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is PkgPage page)
            await page.StartQueueAsync();
    }

    private async void PkgTestPs4_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is PkgPage page)
            await page.TestPs4Async();
    }

    private async void PkgScanDefaultFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is PkgPage page)
            await page.ScanDefaultFolderAsync();
    }

    private async void PkgRetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is PkgPage page)
            await page.RetryFailedAsync();
    }

    private async void PkgClearFinished_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is PkgPage page)
            await page.ClearFinishedAsync();
    }

    private async void HomebrewRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is HomebrewPage page)
            await page.RefreshCatalogAsync();
    }

    private async void SettingsFindPs4_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is SettingsPage page)
            await page.FindPs4Async();
    }

    private async void SettingsTestPs4_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is SettingsPage page)
            await page.TestPs4Async();
    }

    private void SettingsOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is SettingsPage page)
            page.OpenLogs();
    }

    private void SettingsOpenData_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is SettingsPage page)
            page.OpenData();
    }

    private void ShowNavigationFailure(
        string pageName,
        Exception exception)
    {
        ShowShellInfo(
            InfoBarSeverity.Error,
            $"Unable to open {pageName}",
            exception.Message);
    }

    private void ShowShellInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        ShellInfoBar.Severity = severity;
        ShellInfoBar.Title = title;
        ShellInfoBar.Message = message;
        ShellInfoBar.IsOpen = true;
    }

    private async void MainWindow_Closed(
        object sender,
        WindowEventArgs args)
    {
        if (_closing)
            return;

        _closing = true;

        try
        {
            await App.Services.ShutdownAsync();
        }
        catch
        {
            // The window is already closing.
        }
    }
}
