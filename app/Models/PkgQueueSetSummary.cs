namespace PkgRelay.Models;

public sealed class PkgQueueSetSummary
{
    public string TitleId { get; set; } = "";
    public string Title { get; set; } = "";
    public int BaseCount { get; set; }
    public int UpdateCount { get; set; }
    public int DlcCount { get; set; }
    public int HomebrewCount { get; set; }
    public int TotalCount { get; set; }

    public string Header =>
        string.IsNullOrWhiteSpace(TitleId)
            ? Title
            : $"{Title}  ·  {TitleId}";

    public string CompatibilityText
    {
        get
        {
            if (BaseCount == 0 && (UpdateCount > 0 || DlcCount > 0))
                return "Base package not present in queue";

            if (BaseCount > 1)
                return "Multiple base packages detected";

            return "";
        }
    }

    public bool HasCompatibilityWarning =>
        !string.IsNullOrWhiteSpace(CompatibilityText);

    public Microsoft.UI.Xaml.Visibility CompatibilityWarningVisibility =>
        HasCompatibilityWarning
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string Summary
    {
        get
        {
            var pieces = new List<string>();

            if (BaseCount > 0)
                pieces.Add($"{BaseCount} base");

            if (UpdateCount > 0)
                pieces.Add($"{UpdateCount} update{(UpdateCount == 1 ? "" : "s")}");

            if (DlcCount > 0)
                pieces.Add($"{DlcCount} DLC");

            if (HomebrewCount > 0)
                pieces.Add($"{HomebrewCount} homebrew");

            return pieces.Count == 0
                ? $"{TotalCount} package{(TotalCount == 1 ? "" : "s")}"
                : string.Join("  •  ", pieces);
        }
    }
}
