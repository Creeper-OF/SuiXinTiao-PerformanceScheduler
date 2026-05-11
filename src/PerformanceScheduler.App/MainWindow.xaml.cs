using System.Windows;
using System.Windows.Input;
using PerformanceScheduler.App.Ui;
using PerformanceScheduler.App.ViewModels;

namespace PerformanceScheduler.App;

public partial class MainWindow : Window
{
    private bool _isLanguageSwitchAnimating;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ToggleMaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void LanguageSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLanguageSwitchAnimating)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel viewModel ||
            !viewModel.CycleLanguageCommand.CanExecute(null))
        {
            return;
        }

        _isLanguageSwitchAnimating = true;
        var languageChanged = false;
        LanguageTransitionPlan? transitionPlan = null;

        try
        {
            Keyboard.ClearFocus();

            var languageAnimationEnabled = viewModel.LanguageTransitionAnimationEnabled;
            var totalDurationSeconds = Math.Clamp(viewModel.LanguageTransitionDurationSeconds, 0.2, 10);

            if (languageAnimationEnabled)
            {
                var textBlocks = LanguageFlipTextBehavior.CollectVisibleTextBlocks(this);
                transitionPlan = LanguageTransitionCoordinator.CreatePlan(textBlocks, totalDurationSeconds);
                await AwaitLanguageAnimationAsync(
                    LanguageFlipTextBehavior.AnimateCurrentLanguageOutAsync(transitionPlan),
                    transitionPlan.OutBudget);
            }

            if (languageAnimationEnabled && transitionPlan is not null)
            {
                LanguageTransitionCoordinator.IsEnabled = true;
                LanguageTransitionCoordinator.BeginInTransition(transitionPlan);
                LanguageFlipTextBehavior.PrepareNewLanguageIn(transitionPlan);
            }

            viewModel.CycleLanguageCommand.Execute(null);
            languageChanged = true;

            if (languageAnimationEnabled && transitionPlan is not null)
            {
                await AwaitLanguageAnimationAsync(
                    LanguageFlipTextBehavior.AnimateNewLanguageInAsync(transitionPlan),
                    transitionPlan.InBudget);
            }
        }
        catch
        {
            if (!languageChanged && viewModel.CycleLanguageCommand.CanExecute(null))
            {
                viewModel.CycleLanguageCommand.Execute(null);
            }
        }
        finally
        {
            if (transitionPlan is not null)
            {
                LanguageFlipTextBehavior.ResetTextBlocks(transitionPlan);
            }

            _isLanguageSwitchAnimating = false;
        }
    }

    private static async Task AwaitLanguageAnimationAsync(Task animationTask, TimeSpan expectedDuration)
    {
        var timeout = expectedDuration + TimeSpan.FromMilliseconds(900);
        try
        {
            await animationTask.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
        }
    }
}
