using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PkgRelay.Services;
using Windows.Storage.Pickers;

namespace PkgRelay.Views;

public sealed partial class FpkgiDatabaseTool : UserControl
{
    private CancellationTokenSource? operation;
    public Func<FpkgiDatabaseKind, string, IProgress<double>, CancellationToken, Task>? TransferAsync { get; set; }
    public FpkgiDatabaseTool()
    {
        InitializeComponent();
        GamesSourceBox.Text = App.Services.Settings.Current.FpkgiGamesDatabaseUrl;
        DlcSourceBox.Text = App.Services.Settings.Current.FpkgiDlcDatabaseUrl;
        DatabaseFolderText.Text = App.Services.FpkgiDatabases.Folder;
        Loaded += async (_, _) =>
        {
            UploadButton.Visibility = TransferAsync is null ? Visibility.Collapsed : Visibility.Visible;
            await RefreshStatusAsync();
        };
    }
    private FpkgiDatabaseKind[] SelectedKinds() => new[]
    {
        (FpkgiDatabaseKind.Games, GamesCheckBox.IsChecked == true),
        (FpkgiDatabaseKind.Dlc, DlcCheckBox.IsChecked == true),
        (FpkgiDatabaseKind.Homebrew, HomebrewCheckBox.IsChecked == true)
    }.Where(x => x.Item2).Select(x => x.Item1).ToArray();
    private async Task RefreshStatusAsync()
    {
        foreach (var (kind, label) in new[] { (FpkgiDatabaseKind.Games, GamesStatus), (FpkgiDatabaseKind.Dlc, DlcStatus), (FpkgiDatabaseKind.Homebrew, HomebrewStatus) })
        {
            try
            {
                var info = await App.Services.FpkgiDatabases.InspectAsync(kind);
                label.Text = $"{info.Entries:N0} entries • {info.Bytes / 1024d:N0} KB • Saved {info.Updated:g}";
            }
            catch (FileNotFoundException) { label.Text = "Not downloaded / generated"; }
            catch (DirectoryNotFoundException) { label.Text = "Not downloaded / generated"; }
            catch (Exception ex) { label.Text = "Needs attention: " + ex.Message; }
        }
    }
    public Task DownloadSelectedAsync() => RunAsync("Database download", async token =>
    {
        await SaveSourcesAsync();
        var selected = SelectedKinds().Where(x => x != FpkgiDatabaseKind.Homebrew).ToArray();
        await App.Services.FpkgiDatabases.DownloadAsync(selected, new Progress<FpkgiDatabaseProgress>(x =>
        { DatabaseProgress.IsIndeterminate = false; DatabaseProgress.Value = x.Percent; DatabaseProgressText.Text = x.Message; }), token);
        return "Selected Games and DLC databases downloaded, validated and ready for FPKGi.";
    });
    private async void Download_Click(object sender, RoutedEventArgs args) => await DownloadSelectedAsync();
    private async void GenerateHomebrew_Click(object sender, RoutedEventArgs args) => await RunAsync("Homebrew database", async token =>
    {
        var catalog = await App.Services.Homebrew.LoadAsync(token);
        await App.Services.FpkgiDatabases.GenerateHomebrewAsync(HomebrewCatalog.Group(catalog).Select(x => x.SelectedVersion), token);
        return "HOMEBREW.json generated from the latest stable catalog versions.";
    });
    private async void Validate_Click(object sender, RoutedEventArgs args) => await RunAsync("Database validation", async token =>
    {
        var selected = RequireSelection();
        foreach (var kind in selected) await App.Services.FpkgiDatabases.InspectAsync(kind, token);
        return $"{selected.Length} selected database(s) passed validation.";
    });
    private FpkgiDatabaseKind[] RequireSelection()
    {
        var selected = SelectedKinds();
        if (selected.Length == 0) throw new InvalidOperationException("Select at least one database.");
        return selected;
    }
    private async void OpenFolder_Click(object sender, RoutedEventArgs args) => await RunAsync("Database folder", async token =>
    {
        var folder = App.Services.FpkgiDatabases.Folder;
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "SETUP.txt"), "Open FPKGi once, then close it.\nCopy the desired GAMES.json, DLC.json and/or HOMEBREW.json to /user/data/FPKGi/ContentJSONs/ using GoldHEN FTP.\nReopen FPKGi. Use Direct Download for GitHub-hosted packages.\nThe .bak files are previous local versions. Remote backups, when a transfer is made by Orbis Toolz, are kept in PS4-backups.\n", token);
        await Windows.System.Launcher.LaunchFolderPathAsync(folder);
        return "Database folder opened. Copy the JSON files to FPKGi's ContentJSONs folder.";
    });
    private async void Import_Click(object sender, RoutedEventArgs args) => await RunAsync("Database import", async token =>
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); picker.FileTypeFilter.Add(".bak");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return "Import canceled.";
        await App.Services.FpkgiDatabases.ImportAsync((FpkgiDatabaseKind)ImportKindBox.SelectedIndex, file.Path, token);
        return "Database imported and validated. The previous local version was backed up.";
    });
    private async void Restore_Click(object sender, RoutedEventArgs args) => await RunAsync("Database restore", async token =>
    {
        await App.Services.FpkgiDatabases.RestoreAsync((FpkgiDatabaseKind)ImportKindBox.SelectedIndex, token);
        return "The previous local database was restored.";
    });
    private async void Upload_Click(object sender, RoutedEventArgs args) => await RunAsync("PS4 database transfer", async token =>
    {
        if (TransferAsync is null) throw new InvalidOperationException("Use the database folder and FTP instructions to copy files to your PS4.");
        var selected = RequireSelection();
        foreach (var kind in selected) await App.Services.FpkgiDatabases.InspectAsync(kind, token);
        var address = App.Services.Settings.Current.Ps4Address;
        for (var i = 0; i < selected.Length; i++)
        {
            if (App.Services.Settings.Current.Ps4Address != address) throw new InvalidOperationException("The PS4 address changed during transfer. Retry with the selected console.");
            var index = i; var kind = selected[i];
            DatabaseProgressText.Text = "Sending " + FpkgiDatabaseService.FileName(kind);
            await TransferAsync(kind, App.Services.FpkgiDatabases.PathFor(kind), new Progress<double>(x =>
            { DatabaseProgress.IsIndeterminate = false; DatabaseProgress.Value = (index + x / 100) * 100 / selected.Length; }), token);
        }
        return "Selected databases sent to the PS4. Reopen FPKGi to load them.";
    });
    private async Task SaveSourcesAsync()
    {
        var games = FpkgiDatabaseService.SourceUri(GamesSourceBox.Text).AbsoluteUri;
        var dlc = FpkgiDatabaseService.SourceUri(DlcSourceBox.Text).AbsoluteUri;
        App.Services.Settings.Current.FpkgiGamesDatabaseUrl = games;
        App.Services.Settings.Current.FpkgiDlcDatabaseUrl = dlc;
        await App.Services.Settings.SaveAsync();
    }
    private async void SaveSources_Click(object sender, RoutedEventArgs args) => await RunAsync("Database sources", async token => { await SaveSourcesAsync(); return "Download sources saved."; });
    private async void Defaults_Click(object sender, RoutedEventArgs args) => await RunAsync("Database sources", async token =>
    {
        GamesSourceBox.Text = FpkgiDatabaseService.DefaultGamesUrl; DlcSourceBox.Text = FpkgiDatabaseService.DefaultDlcUrl;
        await SaveSourcesAsync(); return "Default Archive.org Games and DLC sources restored.";
    });
    private void Cancel_Click(object sender, RoutedEventArgs args) => operation?.Cancel();
    private async Task RunAsync(string title, Func<CancellationToken, Task<string>> action)
    {
        if (operation is not null) return;
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        operation = request; DatabaseActions.IsEnabled = false;
        DatabaseProgress.Visibility = DatabaseProgressText.Visibility = CancelButton.Visibility = Visibility.Visible;
        DatabaseProgress.IsIndeterminate = true; DatabaseProgressText.Text = title;
        try { ShowInfo(title, await action(request.Token), InfoBarSeverity.Success); }
        catch (OperationCanceledException) { ShowInfo(title, "Canceled. Completed files remain available; incomplete downloads are removed.", InfoBarSeverity.Informational); }
        catch (Exception ex) { ShowInfo(title, ex.Message, InfoBarSeverity.Error); }
        finally
        {
            operation = null; DatabaseActions.IsEnabled = true;
            DatabaseProgress.Visibility = DatabaseProgressText.Visibility = CancelButton.Visibility = Visibility.Collapsed;
            await RefreshStatusAsync();
        }
    }
    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    { DatabaseInfoBar.Title = title; DatabaseInfoBar.Message = message; DatabaseInfoBar.Severity = severity; DatabaseInfoBar.IsOpen = true; }
}
