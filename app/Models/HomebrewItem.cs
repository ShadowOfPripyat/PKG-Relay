using PkgRelay.Infrastructure;

namespace PkgRelay.Models;

public sealed class HomebrewItem : ObservableObject
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Category { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public long Size { get; set; }

    public string Subtitle
    {
        get
        {
            var pieces = new[] { Version, Author, Category }
                .Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join(" • ", pieces);
        }
    }
}
