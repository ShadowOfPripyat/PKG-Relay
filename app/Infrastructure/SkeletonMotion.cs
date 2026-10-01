using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
namespace PkgRelay.Infrastructure;

internal static class SkeletonMotion
{
    internal static void SetActive(UIElement element, bool active)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        if (!active || !NativeMotion.IsEnabled) return;
        var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0, 0.45f); pulse.InsertKeyFrame(0.5f, 0.85f); pulse.InsertKeyFrame(1, 0.45f);
        pulse.Duration = TimeSpan.FromMilliseconds(1400);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", pulse);
    }
}
