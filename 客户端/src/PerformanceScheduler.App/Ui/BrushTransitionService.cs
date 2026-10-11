using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PerformanceScheduler.App.Ui;

public static class BrushTransitionService
{
    public static void UpdateResource(
        string key,
        Color targetColor,
        bool animationEnabled,
        double durationMilliseconds)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var currentColor = resources[key] is SolidColorBrush existingBrush
            ? existingBrush.Color
            : targetColor;
        if (!animationEnabled || !SystemParameters.ClientAreaAnimation || currentColor == targetColor)
        {
            resources[key] = new SolidColorBrush(targetColor);
            return;
        }

        try
        {
            // XAML resources are commonly frozen by WPF. Start the animation before
            // publishing the replacement brush so the resource cannot be frozen.
            var brush = new SolidColorBrush(currentColor);
            var duration = TimeSpan.FromMilliseconds(NormalizeDuration(durationMilliseconds));
            var animation = new ColorAnimation(targetColor, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += (_, _) =>
            {
                if (ReferenceEquals(resources[key], brush))
                {
                    resources[key] = new SolidColorBrush(targetColor);
                }
            };

            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
            resources[key] = brush;
        }
        catch (InvalidOperationException)
        {
            // Theme transitions are cosmetic; a static resource update is the safe fallback.
            resources[key] = new SolidColorBrush(targetColor);
        }
    }

    private static double NormalizeDuration(double durationMilliseconds)
    {
        if (double.IsNaN(durationMilliseconds) || double.IsInfinity(durationMilliseconds))
        {
            return 280;
        }

        return Math.Clamp(durationMilliseconds, 120, 500);
    }
}
