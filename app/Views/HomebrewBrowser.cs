using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PkgRelay.Services;
namespace PkgRelay.Views;

internal static class HomebrewBrowser
{
    internal static bool IsRuntimeAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString(null)); }
        catch { return false; }
    }

    internal static StackPanel ManualDownloadContent(Uri uri, string extension, string reason)
    {
        var instructions = extension == ".db"
            ? "Download store.db in your browser, complete the website's verification, then close this dialog and select Import catalog."
            : "Download the PKG in your browser, complete the website's verification, then close this dialog and select Packages → Add PKG.";
        var panel = new StackPanel { Spacing = 12, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = reason + " " + instructions, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new HyperlinkButton { Content = "Open download in your browser", NavigateUri = uri });
        panel.Children.Add(new HyperlinkButton { Content = "Get Microsoft WebView2 Runtime", NavigateUri = new Uri("https://developer.microsoft.com/microsoft-edge/webview2/#download-section") });
        return panel;
    }

    internal static async Task<string?> DownloadAsync(XamlRoot root, SettingsService settings, Uri uri, string extension, string title, CancellationToken cancellationToken = default)
    {
        if (!HomebrewCatalog.IsHttp(uri)) throw new ArgumentException("A browser download needs HTTP or HTTPS.");
        if (!IsRuntimeAvailable())
        {
            await new ContentDialog { XamlRoot = root, Title = "WebView2 Runtime is unavailable", Content = ManualDownloadContent(uri, extension, "The embedded browser needs Microsoft WebView2 Runtime."), CloseButtonText = "Close" }.ShowAsync();
            return null;
        }
        var folder = Path.Combine(settings.DataDirectory, "Homebrew", "BrowserDownloads");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
        string? result = null;
        var closed = false;
        CoreWebView2DownloadOperation? operation = null;
        var browser = new WebView2 { MinHeight = 400, Height = 470, Width = 820 };
        var message = new TextBlock { Text = "Complete the website's verification if requested. The download will continue here automatically.", TextWrapping = TextWrapping.Wrap };
        var open = new HyperlinkButton { Content = "Open in your browser", NavigateUri = uri };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(message); panel.Children.Add(open); panel.Children.Add(browser);
        var dialog = new ContentDialog { XamlRoot = root, Title = title, Content = panel, CloseButtonText = "Cancel", MaxWidth = 900 };
        dialog.Resources["ContentDialogMaxWidth"] = 900d;
        var log = new LogService(settings);
        Exception? initializationError = null;
        browser.CoreWebView2Initialized += (_, args) => initializationError = args.Exception;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                await log.InfoAsync("Homebrew browser: initializing WebView2.");
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(settings.DataDirectory, "Homebrew", "BrowserProfile"), null);
                if (closed) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (closed) return;
                if (browser.CoreWebView2 is null) throw initializationError ?? new InvalidOperationException("WebView2 did not initialize. Install or repair the Microsoft WebView2 Runtime and restart the app.");
                await log.InfoAsync("Homebrew browser: ready for navigation.");
                browser.CoreWebView2.NewWindowRequested += (_, args) => { args.Handled = true; if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && HomebrewCatalog.IsHttp(target)) browser.Source = target; };
                browser.CoreWebView2.DownloadStarting += (_, args) =>
                {
                    if (operation is not null) { args.Cancel = true; return; }
                    args.Handled = true;
                    args.ResultFilePath = destination;
                    operation = args.DownloadOperation;
                    operation.StateChanged += (_, _) =>
                    {
                        if (closed) return;
                        if (operation.State == CoreWebView2DownloadState.Completed) { result = destination; dialog.Hide(); }
                        else if (operation.State == CoreWebView2DownloadState.Interrupted) message.Text = "Download interrupted: " + operation.InterruptReason + ". Cancel and try again, or save the catalog in your browser and use Import catalog.";
                    };
                };
                browser.Source = uri;
            }
            catch (Exception ex) { await log.WarningAsync("Homebrew browser startup failed: " + ex); if (!closed) dialog.Content = ManualDownloadContent(uri, extension, "The embedded browser could not start: " + ex.Message + "."); }
        };
        using var registration = cancellationToken.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        try { await dialog.ShowAsync(); cancellationToken.ThrowIfCancellationRequested(); return result; }
        finally
        {
            closed = true;
            if (result is null) operation?.Cancel();
            browser.Close();
            if (result is null) { try { File.Delete(destination); } catch (IOException) { } }
        }
    }
}
