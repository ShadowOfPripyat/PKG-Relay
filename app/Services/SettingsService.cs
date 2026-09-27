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

    public string DataDirectory => _directory;
    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PKG Relay");

        _path = Path.Combine(_directory, "settings.json");
    }

    public async Task LoadAsync()
    {
        Directory.CreateDirectory(_directory);

        if (!File.Exists(_path))
        {
            await SaveAsync();
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
