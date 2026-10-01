using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using PkgRelay.Models;

namespace PkgRelay.Views;

public sealed partial class InstallStatusBadge : UserControl
{
    public InstallStatusBadge() => InitializeComponent();

    public static readonly DependencyProperty JobProperty = DependencyProperty.Register(
        nameof(Job), typeof(InstallJob), typeof(InstallStatusBadge), new PropertyMetadata(null));

    public InstallJob? Job
    {
        get => (InstallJob?)GetValue(JobProperty);
        set => SetValue(JobProperty, value);
    }
}
