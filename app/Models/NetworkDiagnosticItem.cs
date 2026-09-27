namespace PkgRelay.Models;

public enum DiagnosticSeverity
{
    Success,
    Warning,
    Error
}

public sealed record NetworkDiagnosticItem(
    DiagnosticSeverity Severity,
    string Name,
    string Detail);

public sealed record NetworkDiagnosticReport(
    IReadOnlyList<NetworkDiagnosticItem> Items)
{
    public bool HasErrors =>
        Items.Any(x => x.Severity == DiagnosticSeverity.Error);

    public bool HasWarnings =>
        Items.Any(x => x.Severity == DiagnosticSeverity.Warning);
}
