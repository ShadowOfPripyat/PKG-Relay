using System.Text.Json;
using PkgRelay.Models;

namespace PkgRelay.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public string DataDirectory => _directory;
    public AppSettings Current { get; private set; } = new();

    public SettingsService() : this(null)
    {
    }

    internal SettingsService(string? dataDirectory)
    {
        _directory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PKG Relay");

        _path = Path.Combine(_directory, "settings.json");
    }

    public async Task LoadAsync()
    {
        await _ioGate.WaitAsync();
        try { await LoadCoreAsync(); }
        finally { _ioGate.Release(); }
    }

    private async Task LoadCoreAsync()
    {
        Directory.CreateDirectory(_directory);

        if (!File.Exists(_path))
        {
            await SaveCoreAsync();
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            Current =
                await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    JsonOptions)
                ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        await _ioGate.WaitAsync();
        try { await SaveCoreAsync(); }
        finally { _ioGate.Release(); }
    }

    private async Task SaveCoreAsync()
    {
        Directory.CreateDirectory(_directory);

        var temporary = _path + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                Current,
                JsonOptions);
        }

        File.Move(temporary, _path, true);
    }
}
