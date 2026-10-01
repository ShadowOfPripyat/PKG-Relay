using System.ComponentModel;
namespace PkgRelay.Models;

public sealed class HomebrewApp : INotifyPropertyChanged
{
    public IReadOnlyList<HomebrewItem> Versions { get; }
    private HomebrewItem selected;
    public HomebrewApp(IReadOnlyList<HomebrewItem> versions)
    {
        if (versions.Count == 0) throw new ArgumentException("An app needs at least one version.", nameof(versions));
        Versions = versions;
        selected = versions[0];
    }
    public HomebrewItem SelectedVersion
    {
        get => selected;
        set
        {
            if (value is null || !Versions.Contains(value) || ReferenceEquals(value, selected)) return;
            selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
    public string Name => Versions[0].Name;
    public string Category => Versions[0].Category;
    public string ImageUrl => selected.ImageUrl.Length > 0 ? selected.ImageUrl : Versions.Select(x => x.ImageUrl).FirstOrDefault(x => x.Length > 0) ?? "";
    public string SecondaryText => selected.SecondaryText;
    public string Description => selected.Description;
    public string FirmwareText => selected.FirmwareText;
    public string DateText => selected.DateText;
    public string SizeText => selected.SizeText;
    public string SourceText => selected.SourceName.Length > 0 ? $"Download: {selected.SourceName}" : "Catalog download";
    public event PropertyChangedEventHandler? PropertyChanged;
}
