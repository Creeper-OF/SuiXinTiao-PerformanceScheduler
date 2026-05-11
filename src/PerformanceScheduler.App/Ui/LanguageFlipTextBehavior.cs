using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PerformanceScheduler.App.Ui;

public static class LanguageFlipTextBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(LanguageFlipTextBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty LastSequenceProperty =
        DependencyProperty.RegisterAttached(
            "LastSequence",
            typeof(int),
            typeof(LanguageFlipTextBehavior),
            new PropertyMetadata(0));

    public static readonly DependencyProperty ExcludeFromTransitionProperty =
        DependencyProperty.RegisterAttached(
            "ExcludeFromTransition",
            typeof(bool),
            typeof(LanguageFlipTextBehavior),
            new PropertyMetadata(false));

    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    public static bool GetExcludeFromTransition(DependencyObject element)
    {
        return (bool)element.GetValue(ExcludeFromTransitionProperty);
    }

    public static void SetExcludeFromTransition(DependencyObject element, bool value)
    {
        element.SetValue(ExcludeFromTransitionProperty, value);
    }

    private static int GetLastSequence(DependencyObject element)
    {
        return (int)element.GetValue(LastSequenceProperty);
    }

    private static void SetLastSequence(DependencyObject element, int value)
    {
        element.SetValue(LastSequenceProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not TextBlock textBlock)
        {
            return;
        }

        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        if ((bool)e.NewValue)
        {
            descriptor.AddValueChanged(textBlock, OnTextChanged);
            return;
        }

        descriptor.RemoveValueChanged(textBlock, OnTextChanged);
    }

    public static IReadOnlyList<TextBlock> CollectVisibleTextBlocks(DependencyObject root)
    {
        var textBlocks = new List<TextBlock>();
        CollectVisibleTextBlocks(root, textBlocks);
        return textBlocks;
    }

    public static Task AnimateCurrentLanguageOutAsync(LanguageTransitionPlan plan)
    {
        if (plan.OutTimings.Count == 0)
        {
            return Task.CompletedTask;
        }

        var animations = new List<Task>(plan.OutTimings.Count);
        foreach (var (textBlock, timing) in plan.OutTimings)
        {
            animations.Add(AnimateTextBlockOutAsync(textBlock, timing));
        }

        return Task.WhenAll(animations);
    }

    public static Task AnimateNewLanguageInAsync(LanguageTransitionPlan plan)
    {
        if (plan.InTimings.Count == 0)
        {
            return Task.CompletedTask;
        }

        var animations = new List<Task>(plan.InTimings.Count);
        foreach (var (textBlock, timing) in plan.InTimings)
        {
            SetLastSequence(textBlock, plan.Sequence);
            animations.Add(AnimateTextBlockInAsync(textBlock, timing));
        }

        return Task.WhenAll(animations);
    }

    public static void PrepareNewLanguageIn(LanguageTransitionPlan plan)
    {
        foreach (var textBlock in plan.InTimings.Keys)
        {
            SetLastSequence(textBlock, plan.Sequence);
        }
    }

    public static void ResetTextBlocks(LanguageTransitionPlan plan)
    {
        foreach (var textBlock in plan.TextBlocks)
        {
            ResetTextBlockVisualState(textBlock);
        }
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock textBlock ||
            !LanguageTransitionCoordinator.TryCreateTiming(textBlock, out var sequence, out var delay, out var duration, out var direction) ||
            GetLastSequence(textBlock) == sequence)
        {
            return;
        }

        SetLastSequence(textBlock, sequence);
        _ = AnimateTextBlockInAsync(textBlock, new LanguageTextTiming(delay, duration, direction));
    }

    private static void CollectVisibleTextBlocks(DependencyObject root, ICollection<TextBlock> textBlocks)
    {
        if (GetExcludeFromTransition(root))
        {
            return;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TextBlock textBlock &&
                textBlock.IsVisible &&
                !string.IsNullOrEmpty(textBlock.Text))
            {
                textBlocks.Add(textBlock);
            }

            CollectVisibleTextBlocks(child, textBlocks);
        }
    }

    private static Task AnimateTextBlockInAsync(TextBlock textBlock, LanguageTextTiming timing)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transform = new TransformGroup();
        var scale = new ScaleTransform(1, 0.86);
        var translate = new TranslateTransform(0, -timing.Direction * 12);
        transform.Children.Add(scale);
        transform.Children.Add(translate);

        textBlock.RenderTransformOrigin = new Point(0.5, 0.5);
        textBlock.RenderTransform = transform;
        textBlock.Opacity = 0;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation(1, timing.Duration)
        {
            BeginTime = timing.Delay,
            EasingFunction = easing
        };
        var translateAnimation = new DoubleAnimation(0, timing.Duration)
        {
            BeginTime = timing.Delay,
            EasingFunction = easing
        };
        var scaleAnimation = new DoubleAnimation(1, timing.Duration)
        {
            BeginTime = timing.Delay,
            EasingFunction = easing
        };

        opacityAnimation.Completed += (_, _) =>
        {
            ResetTextBlockVisualState(textBlock);
            completion.TrySetResult();
        };

        textBlock.BeginAnimation(UIElement.OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, translateAnimation, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation, HandoffBehavior.SnapshotAndReplace);

        return completion.Task;
    }

    private static Task AnimateTextBlockOutAsync(TextBlock textBlock, LanguageTextTiming timing)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transform = new TransformGroup();
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        transform.Children.Add(scale);
        transform.Children.Add(translate);

        textBlock.RenderTransformOrigin = new Point(0.5, 0.5);
        textBlock.RenderTransform = transform;

        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opacityAnimation = new DoubleAnimation(0, timing.Duration)
        {
            BeginTime = timing.Delay,
            EasingFunction = easing
        };
        var translateAnimation = new DoubleAnimation(timing.Direction * 12, timing.Duration)
        {
            BeginTime = timing.Delay,
            EasingFunction = easing
        };
        var scaleAnimation = new DoubleAnimation(0.86, timing.Duration)
        {
            BeginTime = timing.Delay,
            EasingFunction = easing
        };

        opacityAnimation.Completed += (_, _) =>
        {
            textBlock.Opacity = 0;
            completion.TrySetResult();
        };

        textBlock.BeginAnimation(UIElement.OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        translate.BeginAnimation(TranslateTransform.YProperty, translateAnimation, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation, HandoffBehavior.SnapshotAndReplace);

        return completion.Task;
    }

    private static void ResetTextBlockVisualState(TextBlock textBlock)
    {
        textBlock.BeginAnimation(UIElement.OpacityProperty, null);
        textBlock.Opacity = 1;
        textBlock.RenderTransform = Transform.Identity;
    }
}
