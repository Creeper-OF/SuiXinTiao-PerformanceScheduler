using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PerformanceScheduler.App.Ui;

public static class StaggeredRevealBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(StaggeredRevealBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty DurationMillisecondsProperty =
        DependencyProperty.RegisterAttached(
            "DurationMilliseconds",
            typeof(double),
            typeof(StaggeredRevealBehavior),
            new PropertyMetadata(280d));

    private static readonly DependencyProperty HasAnimatedProperty =
        DependencyProperty.RegisterAttached(
            "HasAnimated",
            typeof(bool),
            typeof(StaggeredRevealBehavior),
            new PropertyMetadata(false));

    private static readonly DependencyProperty AnimationSequenceProperty =
        DependencyProperty.RegisterAttached(
            "AnimationSequence",
            typeof(int),
            typeof(StaggeredRevealBehavior),
            new PropertyMetadata(0));

    private static readonly DependencyProperty OriginalOpacityProperty =
        DependencyProperty.RegisterAttached(
            "OriginalOpacity",
            typeof(double),
            typeof(StaggeredRevealBehavior),
            new PropertyMetadata(double.NaN));

    private static readonly DependencyProperty OriginalRenderTransformProperty =
        DependencyProperty.RegisterAttached(
            "OriginalRenderTransform",
            typeof(Transform),
            typeof(StaggeredRevealBehavior),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static double GetDurationMilliseconds(DependencyObject element) =>
        (double)element.GetValue(DurationMillisecondsProperty);

    public static void SetDurationMilliseconds(DependencyObject element, double value) =>
        element.SetValue(DurationMillisecondsProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Panel panel)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            panel.Loaded += OnLoaded;
            if (panel.IsLoaded)
            {
                QueueReveal(panel);
            }

            return;
        }

        panel.Loaded -= OnLoaded;
        panel.SetValue(HasAnimatedProperty, false);
        panel.SetValue(AnimationSequenceProperty, (int)panel.GetValue(AnimationSequenceProperty) + 1);
        ResetChildren(panel);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Panel panel)
        {
            QueueReveal(panel);
        }
    }

    private static void QueueReveal(Panel panel)
    {
        if (!GetIsEnabled(panel) ||
            !SystemParameters.ClientAreaAnimation ||
            (bool)panel.GetValue(HasAnimatedProperty))
        {
            return;
        }

        panel.SetValue(HasAnimatedProperty, true);
        _ = panel.Dispatcher.InvokeAsync(() => RevealChildren(panel), DispatcherPriority.Loaded);
    }

    private static void RevealChildren(Panel panel)
    {
        var children = panel.Children
            .OfType<FrameworkElement>()
            .Where(child => child.Visibility == Visibility.Visible)
            .ToArray();
        var durationMilliseconds = NormalizeDuration(GetDurationMilliseconds(panel));
        var delayStepMilliseconds = Math.Clamp(durationMilliseconds * 0.16, 24, 64);
        var sequence = (int)panel.GetValue(AnimationSequenceProperty) + 1;
        panel.SetValue(AnimationSequenceProperty, sequence);

        for (var index = 0; index < children.Length; index++)
        {
            AnimateChild(panel, children[index], index * delayStepMilliseconds, durationMilliseconds, sequence);
        }
    }

    private static void AnimateChild(
        Panel panel,
        FrameworkElement child,
        double delayMilliseconds,
        double durationMilliseconds,
        int sequence)
    {
        child.BeginAnimation(UIElement.OpacityProperty, null);
        var originalOpacity = child.Opacity;
        child.SetValue(OriginalOpacityProperty, originalOpacity);
        child.SetValue(OriginalRenderTransformProperty, child.RenderTransform);
        var translate = new TranslateTransform(0, 12);
        child.RenderTransform = translate;
        child.Opacity = 0;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation(originalOpacity, TimeSpan.FromMilliseconds(durationMilliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
            EasingFunction = easing
        };
        var translateAnimation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(durationMilliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
            EasingFunction = easing
        };

        opacityAnimation.Completed += (_, _) =>
        {
            if ((int)panel.GetValue(AnimationSequenceProperty) == sequence)
            {
                RestoreChild(child);
            }
        };

        child.BeginAnimation(UIElement.OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, translateAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetChildren(Panel panel)
    {
        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            RestoreChild(child);
        }
    }

    private static void RestoreChild(FrameworkElement child)
    {
        child.BeginAnimation(UIElement.OpacityProperty, null);
        var originalOpacity = (double)child.GetValue(OriginalOpacityProperty);
        if (!double.IsNaN(originalOpacity))
        {
            child.Opacity = originalOpacity;
        }

        if (child.GetValue(OriginalRenderTransformProperty) is Transform originalTransform)
        {
            child.RenderTransform = originalTransform;
        }

        child.ClearValue(OriginalOpacityProperty);
        child.ClearValue(OriginalRenderTransformProperty);
    }

    private static double NormalizeDuration(double durationMilliseconds)
    {
        if (double.IsNaN(durationMilliseconds) || double.IsInfinity(durationMilliseconds))
        {
            return 280;
        }

        return Math.Clamp(durationMilliseconds, 120, 800);
    }
}
