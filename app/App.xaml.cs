using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PkgRelay;

public partial class App : Application
{
    public static AppServices Services { get; } = new();

    // Keep this typed as Window so the picker code also works if the
    // diagnostic fallback window has to be used.
    public static Window MainWindow { get; private set; } = null!;

    public static Task InitializationTask { get; private set; } =
        Task.CompletedTask;

    public App()
    {
        InitializeComponent();

        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException +=
            CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException +=
            TaskScheduler_UnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var window = new MainWindow();

            MainWindow = window;

            // Activate the shell BEFORE navigating to any page. A runtime
            // XAML/binding error in PkgPage/HomebrewPage/SettingsPage must
            // never prevent the application's first window from appearing.
            window.Activate();

            Services.AttachDispatcher(window.DispatcherQueue);

            window.InitializeAfterActivation();

            InitializationTask = InitializeServicesAsync(window);
        }
        catch (Exception ex)
        {
            WriteEmergencyLog("Main window startup failed.", ex);
            ShowFallbackWindow(ex);
        }
    }

    private static async Task InitializeServicesAsync(MainWindow window)
    {
        try
        {
            await Services.Settings.LoadAsync();
            window.ApplySettings();

            await Services.Queue.RestoreAsync();
            Services.LibraryWatcher.ApplySettings();

            window.SetAppReady(true);
        }
        catch (Exception ex)
        {
            WriteEmergencyLog("Application initialization failed.", ex);

            try
            {
                await Services.Log.ErrorAsync(
                    "Application initialization failed.",
                    ex);
            }
            catch
            {
                // Emergency logging above must remain the last-resort path.
            }

            window.SetStartupWarning(
                "Startup initialization failed. Open Settings > Diagnostics > Open logs for details.");
        }
    }

    private static void ShowFallbackWindow(Exception exception)
    {
        try
        {
            var fallback = new Window
            {
                Title = "PKG Relay — startup error"
            };

            var message = new TextBlock
            {
                Text =
                    "PKG Relay started, but the main UI failed to load.\n\n" +
                    exception +
                    "\n\nA startup log was written to:\n" +
                    GetEmergencyLogPath(),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };

            fallback.Content = new ScrollViewer
            {
                Content = new Grid
                {
                    Padding = new Thickness(24),
                    Children = { message }
                }
            };

            MainWindow = fallback;
            fallback.Activate();
        }
        catch
        {
            // If WinUI itself cannot initialize, no XAML window can be shown.
            // launchSettings.json + the unpackaged project profile handle the
            // common Visual Studio startup/runtime issue before this point.
        }
    }

    private static void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteEmergencyLog("WinUI unhandled exception.", e.Exception);
        // Do not mark it handled globally. Swallowing arbitrary UI exceptions
        // can leave the process in an invalid state.
    }

    private static void CurrentDomain_UnhandledException(
        object? sender,
        System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            WriteEmergencyLog("CLR unhandled exception.", ex);
        else
            WriteEmergencyLog(
                "CLR unhandled exception.",
                new Exception(e.ExceptionObject?.ToString() ?? "Unknown exception"));
    }

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        WriteEmergencyLog("Unobserved task exception.", e.Exception);
    }

    private static string GetEmergencyLogPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "PKG Relay");

        Directory.CreateDirectory(directory);

        return Path.Combine(directory, "startup-crash.log");
    }

    private static void WriteEmergencyLog(
        string context,
        Exception exception)
    {
        try
        {
            File.AppendAllText(
                GetEmergencyLogPath(),
                $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}" +
                $"BaseDirectory: {AppContext.BaseDirectory}{Environment.NewLine}" +
                $"Runtime: {Environment.Version}{Environment.NewLine}" +
                new string('-', 80) +
                Environment.NewLine);
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }
}
