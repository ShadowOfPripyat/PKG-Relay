using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using PkgRelay.Models;

namespace PkgRelay.Converters;

public sealed class InstallStatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush WaitingBackground =
        new(ColorHelper.FromArgb(0xFF, 0x5C, 0x5C, 0x5C));

    private static readonly SolidColorBrush PreparingBackground =
        new(ColorHelper.FromArgb(0xFF, 0x00, 0x67, 0xC0));

    private static readonly SolidColorBrush SendingBackground =
        new(ColorHelper.FromArgb(0xFF, 0x00, 0x67, 0xC0));

    private static readonly SolidColorBrush InstallingBackground =
        new(ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4));

    private static readonly SolidColorBrush PausedBackground =
        new(ColorHelper.FromArgb(0xFF, 0xFF, 0xB9, 0x00));

    private static readonly SolidColorBrush CompletedBackground =
        new(ColorHelper.FromArgb(0xFF, 0x10, 0x7C, 0x10));

    private static readonly SolidColorBrush FailedBackground =
        new(ColorHelper.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));

    private static readonly SolidColorBrush CancelledBackground =
        new(ColorHelper.FromArgb(0xFF, 0x6B, 0x6B, 0x6B));

    private static readonly SolidColorBrush LightForeground =
        new(Colors.White);

    private static readonly SolidColorBrush DarkForeground =
        new(Colors.Black);

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        var status =
            value is InstallStatus installStatus
                ? installStatus
                : InstallStatus.Waiting;

        var foreground =
            string.Equals(
                parameter?.ToString(),
                "Foreground",
                StringComparison.OrdinalIgnoreCase);

        if (foreground)
        {
            return status == InstallStatus.Paused
                ? DarkForeground
                : LightForeground;
        }

        return status switch
        {
            InstallStatus.Waiting => WaitingBackground,
            InstallStatus.Preparing => PreparingBackground,
            InstallStatus.Sending => SendingBackground,
            InstallStatus.Recovering => SendingBackground,
            InstallStatus.Installing => InstallingBackground,
            InstallStatus.Finalizing => CompletedBackground,
            InstallStatus.Paused => PausedBackground,
            InstallStatus.Completed => CompletedBackground,
            InstallStatus.Failed => FailedBackground,
            InstallStatus.Cancelled => CancelledBackground,
            _ => WaitingBackground
        };
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        throw new NotSupportedException();
    }
}
