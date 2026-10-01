using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PkgRelay.Infrastructure;

public sealed class HomebrewImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        // x:Bind's implicit string-to-ImageSource conversion throws for null
        // artwork. Return a typed source (or null) so the fallback icon remains.
        if (value is not string text || string.IsNullOrWhiteSpace(text) ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return null;
        return new BitmapImage(uri);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
