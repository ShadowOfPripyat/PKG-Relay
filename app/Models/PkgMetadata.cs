namespace PkgRelay.Models;

public enum PkgKind
{
    Unknown,
    Base,
    Update,
    Dlc,
    Homebrew
}

public sealed record PkgMetadata(
    bool IsValid,
    string Title,
    string TitleId,
    string ContentId,
    PkgKind Kind,
    long FileSize,
    string Category = "",
    string AppVersion = "",
    string Version = "",
    uint SystemVersion = 0,
    string RequiredFirmware = "",
    uint ContentFlags = 0);
