using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PkgRelay.Models;
using PkgRelay.Views;

namespace PkgRelay;

public partial class App
{
    private async Task CheckStatusBadgesAsync()
    {
        var job = new InstallJob();
        var badge = new InstallStatusBadge { Job = job };
        window!.Content = badge;
        await Task.Delay(100);
        var background = (Border)badge.FindName("BadgeBackground");
        var label = (TextBlock)badge.FindName("BadgeLabel");
        // Exercise the production compiled bindings, not just the converter.
        foreach (var (status, rgb) in new (InstallStatus, uint)[]
        {
            (InstallStatus.Waiting, 0x5C5C5C), (InstallStatus.Preparing, 0x0067C0),
            (InstallStatus.Sending, 0x0067C0), (InstallStatus.Installing, 0x0078D4),
            (InstallStatus.Paused, 0xFFB900), (InstallStatus.Recovering, 0x0067C0),
            (InstallStatus.Finalizing, 0x107C10), (InstallStatus.Completed, 0x107C10),
            (InstallStatus.Failed, 0xC42B1C), (InstallStatus.Cancelled, 0x6B6B6B)
        })
        {
            job.Status = status;
            await Task.Delay(30);
            AssertColor(background.Background, rgb, status.ToString());
            if (label.Text != job.StatusText) throw new InvalidOperationException("Badge text did not update with " + status);
            AssertColor(label.Foreground, status == InstallStatus.Paused ? 0u : 0xFFFFFFu, "Badge foreground");
        }
        var replacement = new InstallJob { Status = InstallStatus.Completed };
        badge.Job = replacement;
        await Task.Delay(30);
        AssertColor(background.Background, 0x107C10, "Recycled badge");
        job.Status = InstallStatus.Failed;
        await Task.Delay(30);
        AssertColor(background.Background, 0x107C10, "Detached job");
        replacement.Status = InstallStatus.Installing;
        await Task.Delay(30);
        AssertColor(background.Background, 0x0078D4, "Replacement job");
        Log("Native badges: every status, foreground contrast, replacement job and detached job passed");
    }

    private static void AssertColor(Brush brush, uint rgb, string context)
    {
        var expected = ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (brush is not SolidColorBrush solid || solid.Color != expected)
            throw new InvalidOperationException(context + " has the wrong badge color.");
    }
}
