using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PerformanceScheduler.App.Ui;

public static class PageTransitionBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(PageTransitionBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty DurationMillisecondsProperty =
        DependencyProperty.RegisterAttached(
            "DurationMilliseconds",
            typeof(double),
            typeof(PageTransitionBehavior),
            new PropertyMetadata(280d));

    private static readonly DependencyProperty LastSelectedIndexProperty =
        DependencyProperty.RegisterAttached(
            "LastSelectedIndex",
            typeof(int),
            typeof(PageTransitionBehavior),
            new PropertyMetadata(-1));

    private static readonly DependencyProperty AnimationSequenceProperty =
        DependencyProperty.RegisterAttached(
            "AnimationSequence",
            typeof(int),
            typeof(PageTransitionBehavior),
            new PropertyMetadata(0));

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
        if (dependencyObject is not TabControl tabControl)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            tabControl.Loaded += OnLoaded;
            tabControl.SelectionChanged += OnSelectionChanged;
            if (tabControl.IsLoaded)
            {
                AnimateSelectedPage(tabControl, direction: 1);
            }

            return;
        }

        tabControl.Loaded -= OnLoaded;
        tabControl.SelectionChanged -= OnSelectionChanged;
        ResetPresenter(tabControl);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TabControl tabControl || !GetIsEnabled(tabControl))
        {
            return;
        }

        tabControl.SetValue(LastSelectedIndexProperty, tabControl.SelectedIndex);
        AnimateSelectedPage(tabControl, direction: 1);
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabControl ||
            !ReferenceEquals(e.OriginalSource, tabControl) ||
            !GetIsEnabled(tabControl))
        {
            return;
        }

        var previousIndex = (int)tabControl.GetValue(LastSelectedIndexProperty);
        var currentIndex = tabControl.SelectedIndex;
        tabControl.SetValue(LastSelectedIndexProperty, currentIndex);
        AnimateSelectedPage(tabControl, currentIndex >= previousIndex ? 1 : -1);
    }

    private static void AnimateSelectedPage(TabControl tabControl, int direction)
    {
        if (!SystemParameters.ClientAreaAnimation || FindPresenter(tabControl) is not { } presenter)
        {
            ResetPresenter(tabControl);
            return;
        }

        var sequence = (int)tabControl.GetValue(AnimationSequenceProperty) + 1;
        tabControl.SetValue(AnimationSequenceProperty, sequence);

        presenter.BeginAnimation(UIElement.OpacityProperty, null);
        var translate = new TranslateTransform(direction * 22, 6);
        presenter.RenderTransform = translate;
        presenter.Opacity = 0.2;

        var duration = TimeSpan.FromMilliseconds(NormalizeDuration(GetDurationMilliseconds(tabControl)));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation(1, duration)
        {
            EasingFunction = easing
        };
        var horizontalAnimation = new DoubleAnimation(0, duration)
        {
            EasingFunction = easing
        };
        var verticalAnimation = new DoubleAnimation(0, duration)
        {
            EasingFunction = easing
        };

        opacityAnimation.Completed += (_, _) =>
        {
            if ((int)tabControl.GetValue(AnimationSequenceProperty) != sequence)
            {
                return;
            }

            presenter.BeginAnimation(UIElement.OpacityProperty, null);
            presenter.Opacity = 1;
            presenter.RenderTransform = Transform.Identity;
        };

        presenter.BeginAnimation(UIElement.OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.XProperty, horizontalAnimation, HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, verticalAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetPresenter(TabControl tabControl)
    {
        if (FindPresenter(tabControl) is not { } presenter)
        {
            return;
        }

        presenter.BeginAnimation(UIElement.OpacityProperty, null);
        presenter.Opacity = 1;
        presenter.RenderTransform = Transform.Identity;
    }

    private static FrameworkElement? FindPresenter(TabControl tabControl)
    {
        tabControl.ApplyTemplate();
        return tabControl.Template.FindName("PART_SelectedContentHost", tabControl) as FrameworkElement;
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
