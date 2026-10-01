using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
namespace PkgRelay.Infrastructure;
// IsOpen collapses an internal template child. Closing delays that collapse so
// the exit stays visible; implicit animations on the outer control cannot do it.
public static class InfoBarMotion
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(InfoBarMotion), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(object), typeof(InfoBarMotion), new PropertyMetadata(null));
    public static bool GetIsEnabled(DependencyObject value) => (bool)value.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject value, bool enabled) => value.SetValue(IsEnabledProperty, enabled);
    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not InfoBar bar) return;
        (bar.GetValue(StateProperty) as State)?.Dispose();
        bar.SetValue(StateProperty, (bool)args.NewValue ? new State(bar) : null);
    }
    private sealed class State : IDisposable
    {
        private readonly InfoBar bar;
        private readonly long openToken, severityToken, titleToken, messageToken;
        private bool closing, allowClose, pendingChange, disposed;
        private int generation;
        private DateTime lastEntrance;
        internal State(InfoBar bar)
        {
            this.bar = bar;
            ElementCompositionPreview.SetImplicitShowAnimation(bar, null);
            ElementCompositionPreview.SetImplicitHideAnimation(bar, null);
            openToken = bar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OpenChanged);
            severityToken = bar.RegisterPropertyChangedCallback(InfoBar.SeverityProperty, ContentChanged);
            titleToken = bar.RegisterPropertyChangedCallback(InfoBar.TitleProperty, ContentChanged);
            messageToken = bar.RegisterPropertyChangedCallback(InfoBar.MessageProperty, MessageChanged);
            bar.Closing += Closing;
            bar.Loaded += Loaded;
            bar.Unloaded += Unloaded;
        }
        private void Loaded(object sender, RoutedEventArgs args) { if (bar.IsOpen) Enter(); }
        private void OpenChanged(DependencyObject sender, DependencyProperty property) { if (bar.IsOpen && !closing && bar.IsLoaded) Enter(); }
        private void Enter()
        {
            generation++;
            Reset();
            lastEntrance = DateTime.UtcNow;
            if (!NativeMotion.IsEnabled) return;
            var visual = Visual();
            visual.StartAnimation("Translation", Translation(visual.Compositor, -14, 0, 220));
            visual.StartAnimation("Opacity", Fade(visual.Compositor, 0, 1, 220));
        }
        private void Closing(InfoBar sender, InfoBarClosingEventArgs args)
        {
            if (args.Cancel || allowClose || disposed || !bar.IsLoaded || !NativeMotion.IsEnabled) return;
            args.Cancel = true;
            if (closing) return;
            closing = true;
            var current = ++generation;
            var visual = Visual();
            var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            var slide = Translation(visual.Compositor, 0, -14, 160);
            slide.InsertExpressionKeyFrame(0, "this.StartingValue");
            var fade = Fade(visual.Compositor, 1, 0, 160);
            fade.InsertExpressionKeyFrame(0, "this.StartingValue");
            visual.StartAnimation("Translation", slide);
            visual.StartAnimation("Opacity", fade);
            batch.Completed += (_, _) =>
            {
                bar.DispatcherQueue.TryEnqueue(() =>
                {
                    if (disposed || current != generation) return;
                    allowClose = true;
                    try { bar.IsOpen = false; }
                    finally { allowClose = false; closing = false; Reset(); }
                });
                batch.Dispose();
            };
            batch.End();
        }
        private void ContentChanged(DependencyObject sender, DependencyProperty property)
        {
            // Percentage message updates do not replay the state animation.
            if (disposed || !bar.IsLoaded || (!bar.IsOpen && !closing)) return;
            if (closing) { closing = false; generation++; Reset(); }
            if (pendingChange) return;
            pendingChange = true;
            if (!bar.DispatcherQueue.TryEnqueue(() =>
            {
                pendingChange = false;
                if (disposed || !bar.IsOpen || !bar.IsLoaded || !NativeMotion.IsEnabled || DateTime.UtcNow - lastEntrance < TimeSpan.FromMilliseconds(250)) return;
                var visual = Visual();
                var change = visual.Compositor.CreateScalarKeyFrameAnimation();
                change.InsertKeyFrame(0, 1);
                change.InsertKeyFrame(0.35f, 0.65f);
                change.InsertKeyFrame(1, 1);
                change.Duration = TimeSpan.FromMilliseconds(260);
                visual.StartAnimation("Opacity", change);
            })) pendingChange = false;
        }
        private void MessageChanged(DependencyObject sender, DependencyProperty property)
        {
            // New information during an exit must remain visible, even when
            // its severity/title match the preceding notification.
            if (!closing || disposed) return;
            closing = false;
            generation++;
            Reset();
        }
        private Visual Visual()
        {
            ElementCompositionPreview.SetIsTranslationEnabled(bar, true);
            return ElementCompositionPreview.GetElementVisual(bar);
        }
        private void Reset()
        {
            var visual = Visual();
            visual.StopAnimation("Translation");
            visual.StopAnimation("Opacity");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Opacity = 1;
        }
        private void Unloaded(object sender, RoutedEventArgs args)
        {
            generation++;
            if (closing)
            {
                allowClose = true;
                try { bar.IsOpen = false; }
                finally { allowClose = false; closing = false; }
            }
            Reset();
        }
        public void Dispose()
        {
            if (disposed) return;
            Unloaded(bar, new RoutedEventArgs());
            disposed = true;
            bar.UnregisterPropertyChangedCallback(InfoBar.IsOpenProperty, openToken);
            bar.UnregisterPropertyChangedCallback(InfoBar.SeverityProperty, severityToken);
            bar.UnregisterPropertyChangedCallback(InfoBar.TitleProperty, titleToken);
            bar.UnregisterPropertyChangedCallback(InfoBar.MessageProperty, messageToken);
            bar.Closing -= Closing;
            bar.Loaded -= Loaded;
            bar.Unloaded -= Unloaded;
        }
    }
    private static ScalarKeyFrameAnimation Fade(Compositor compositor, float from, float to, int duration)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, from); animation.InsertKeyFrame(1, to);
        animation.Duration = TimeSpan.FromMilliseconds(duration);
        return animation;
    }
    private static Vector3KeyFrameAnimation Translation(Compositor compositor, float from, float to, int duration)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, new Vector3(0, from, 0));
        animation.InsertKeyFrame(1, new Vector3(0, to, 0), compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1)));
        animation.Duration = TimeSpan.FromMilliseconds(duration);
        return animation;
    }
}

