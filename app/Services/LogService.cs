namespace PkgRelay.Services;

public sealed class LogService
{
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string LogDirectory =>
        Path.Combine(_settings.DataDirectory, "Logs");

    public LogService(SettingsService settings)
    {
        _settings = settings;
    }

    public Task InfoAsync(string message) => WriteAsync("INFO", message);
    public Task WarningAsync(string message) => WriteAsync("WARN", message);
    public Task ErrorAsync(string message) => WriteAsync("ERROR", message);

    public async Task ErrorAsync(string message, Exception exception)
    {
        await WriteAsync(
            "ERROR",
            $"{message}{Environment.NewLine}{exception}");
    }

    private async Task WriteAsync(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(
                LogDirectory,
                $"PKG-Relay-{DateTimeOffset.Now:yyyy-MM-dd}.log");

            var line =
                $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";

            await _gate.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(path, line);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // Logging must never crash the sender.
        }
    }
}
