using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace PkgRelay.Infrastructure;

internal static class NativeMotion
{
    internal static bool IsEnabled => new UISettings().AnimationsEnabled;

    internal static void ConfigureShowHide(UIElement element)
    {
        if (!IsEnabled)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(element, null);
            ElementCompositionPreview.SetImplicitHideAnimation(element, null);
            return;
        }
        var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        var show = compositor.CreateAnimationGroup();
        show.Add(Opacity(compositor, 0, 1, 180));
        var hide = compositor.CreateAnimationGroup();
        hide.Add(Opacity(compositor, 1, 0, 100));
        ElementCompositionPreview.SetImplicitShowAnimation(element, show);
        ElementCompositionPreview.SetImplicitHideAnimation(element, hide);
    }

    internal static void Entrance(UIElement element, float horizontalOffset = 18)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!IsEnabled)
            return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var translate = visual.Compositor.CreateVector3KeyFrameAnimation();
        translate.InsertKeyFrame(0, new Vector3(horizontalOffset, 0, 0));
        translate.InsertKeyFrame(1, Vector3.Zero,
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1)));
        translate.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Translation", translate);
        visual.StartAnimation("Opacity", Opacity(visual.Compositor, 0, 1, 180));
    }

    private static ScalarKeyFrameAnimation Opacity(Compositor compositor, float from, float to, int milliseconds)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Target = "Opacity";
        animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, to);
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
        return animation;
    }
}
