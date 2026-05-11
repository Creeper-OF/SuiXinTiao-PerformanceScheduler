using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PerformanceScheduler.App.Ui;

public static class SmoothScrollBehavior
{
    private const double WheelDistanceMultiplier = 0.72;
    private const double MaximumTargetDrift = 600d;
    private const double MinimumAnimationMilliseconds = 220d;
    private const double MaximumAnimationMilliseconds = 460d;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    private static readonly DependencyProperty TargetVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "TargetVerticalOffset",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(0d));

    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    private static double GetAnimatedVerticalOffset(DependencyObject element)
    {
        return (double)element.GetValue(AnimatedVerticalOffsetProperty);
    }

    private static void SetAnimatedVerticalOffset(DependencyObject element, double value)
    {
        element.SetValue(AnimatedVerticalOffsetProperty, value);
    }

    private static double GetTargetVerticalOffset(DependencyObject element)
    {
        return (double)element.GetValue(TargetVerticalOffsetProperty);
    }

    private static void SetTargetVerticalOffset(DependencyObject element, double value)
    {
        element.SetValue(TargetVerticalOffsetProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.Loaded += OnLoaded;
            element.PreviewMouseWheel += OnPreviewMouseWheel;
            return;
        }

        element.Loaded -= OnLoaded;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement dependencyObject)
        {
            return;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(dependencyObject);
        if (scrollViewer is null)
        {
            return;
        }

        SetAnimatedVerticalOffset(dependencyObject, scrollViewer.VerticalOffset);
        SetTargetVerticalOffset(dependencyObject, scrollViewer.VerticalOffset);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement dependencyObject)
        {
            return;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(dependencyObject);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        e.Handled = true;

        var currentOffset = scrollViewer.VerticalOffset;
        var currentTarget = GetTargetVerticalOffset(dependencyObject);
        if (Math.Abs(currentTarget - currentOffset) > MaximumTargetDrift)
        {
            currentTarget = currentOffset;
        }

        var nextOffset = Math.Clamp(currentTarget - e.Delta * WheelDistanceMultiplier, 0, scrollViewer.ScrollableHeight);
        SetTargetVerticalOffset(dependencyObject, nextOffset);

        var travelDistance = Math.Abs(nextOffset - currentOffset);
        var animation = new DoubleAnimation
        {
            From = currentOffset,
            To = nextOffset,
            Duration = TimeSpan.FromMilliseconds(CalculateDurationMilliseconds(travelDistance)),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };

        dependencyObject.BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var scrollViewer = FindDescendant<ScrollViewer>(dependencyObject);
        scrollViewer?.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static double CalculateDurationMilliseconds(double travelDistance)
    {
        var duration = MinimumAnimationMilliseconds + travelDistance * 0.55;
        return Math.Clamp(duration, MinimumAnimationMilliseconds, MaximumAnimationMilliseconds);
    }

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
