using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using PerformanceScheduler.App.Commands;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.App.Ui;
using PerformanceScheduler.App.Views;

namespace PerformanceScheduler.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void ChooseAppearanceBackgroundImage()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All Files (*.*)|*.*",
            Title = T("Dialog.SelectBackgroundImageTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_appearanceAssetsDirectory);
            var extension = Path.GetExtension(dialog.FileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var targetPath = Path.Combine(_appearanceAssetsDirectory, $"window-background{extension.ToLowerInvariant()}");
            CopyFileUnlessSamePath(dialog.FileName, targetPath);

            SelectedAppearanceBackgroundMode = "unified";
            AppearanceBackgroundImagePath = targetPath;
            AppearanceBackgroundImageEnabled = true;
            _settingsStore.Update(settings =>
            {
                settings.AppearanceBackgroundImagePath = targetPath;
                settings.AppearanceBackgroundImageEnabled = true;
                settings.AppearanceBackgroundMode = "unified";
            });
            SetAppearanceStatus("Status.AppearanceBackgroundSelected", targetPath);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to set appearance background image.", exception);
            SetAppearanceStatus("Status.AppearanceBackgroundFailed", exception.Message);
        }
    }

    private void ChooseAppearanceRegionBackgroundImage(string region)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All Files (*.*)|*.*",
            Title = T(region == "sidebar" ? "Dialog.SelectSidebarBackgroundImageTitle" : "Dialog.SelectContentBackgroundImageTitle")
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_appearanceAssetsDirectory);
            var isSidebar = string.Equals(region, "sidebar", StringComparison.OrdinalIgnoreCase);
            var targetFileName = isSidebar
                ? $"sidebar-background-preview-{Guid.NewGuid():N}.png"
                : $"content-background-preview-{Guid.NewGuid():N}.png";
            var targetPath = Path.Combine(_appearanceAssetsDirectory, targetFileName);

            if (!TryCropRegionBackground(dialog.FileName, targetPath, isSidebar ? "sidebar" : "content"))
            {
                SetAppearanceStatus(isSidebar ? "Status.AppearanceSidebarBackgroundPreviewCancelled" : "Status.AppearanceContentBackgroundPreviewCancelled");
                return;
            }

            SelectedAppearanceBackgroundMode = "split";
            AppearanceBackgroundImageEnabled = true;
            if (isSidebar)
            {
                SetPendingAppearanceSidebarBackgroundImagePath(targetPath);
                _settingsStore.Update(settings =>
                {
                    settings.AppearanceBackgroundMode = "split";
                    settings.AppearanceBackgroundImageEnabled = true;
                });
                SetAppearanceStatus("Status.AppearanceSidebarBackgroundPreviewLoaded", targetPath);
                return;
            }

            SetPendingAppearanceContentBackgroundImagePath(targetPath);
            _settingsStore.Update(settings =>
            {
                settings.AppearanceBackgroundMode = "split";
                settings.AppearanceBackgroundImageEnabled = true;
            });
            SetAppearanceStatus("Status.AppearanceContentBackgroundPreviewLoaded", targetPath);
        }
        catch (Exception exception)
        {
            _logger.Error($"Failed to set {region} appearance background image.", exception);
            SetAppearanceStatus("Status.AppearanceBackgroundFailed", exception.Message);
        }
    }

    private bool TryCropRegionBackground(string sourcePath, string targetPath, string region)
    {
        var mainWindow = Application.Current.MainWindow;
        var windowWidth = mainWindow?.ActualWidth > 0 ? mainWindow.ActualWidth : 1180d;
        var windowHeight = mainWindow?.ActualHeight > 0 ? mainWindow.ActualHeight : 720d;
        var isSidebar = string.Equals(region, "sidebar", StringComparison.OrdinalIgnoreCase);
        var targetWidth = isSidebar ? 232d : Math.Max(360d, windowWidth - 232d);
        var targetHeight = Math.Max(420d, windowHeight);
        var cropWindow = new ImageCropWindow(
            sourcePath,
            targetPath,
            targetWidth,
            targetHeight,
            T(isSidebar ? "Dialog.CropSidebarBackgroundTitle" : "Dialog.CropContentBackgroundTitle"),
            T(isSidebar ? "Hint.CropSidebarBackgroundInstruction" : "Hint.CropContentBackgroundInstruction"),
            T(isSidebar ? "Hint.CropSidebarBackgroundFooter" : "Hint.CropContentBackgroundFooter"),
            T("Button.ConfirmCrop"),
            T("Button.Cancel"));

        if (mainWindow is not null)
        {
            cropWindow.Owner = mainWindow;
        }

        return cropWindow.ShowDialog() == true;
    }

    private void ApplyAppearanceSidebarBackgroundPreview()
    {
        if (!HasPendingAppearanceSidebarBackground)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_appearanceAssetsDirectory);
            var extension = Path.GetExtension(_pendingAppearanceSidebarBackgroundImagePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var targetPath = Path.Combine(_appearanceAssetsDirectory, $"sidebar-background{extension.ToLowerInvariant()}");
            var pendingPath = _pendingAppearanceSidebarBackgroundImagePath;
            CopyFileUnlessSamePath(pendingPath, targetPath);

            AppearanceSidebarBackgroundImagePath = targetPath;
            SetPendingAppearanceSidebarBackgroundImagePath(string.Empty);
            DeleteFileIfExists(pendingPath);
            SelectedAppearanceBackgroundMode = "split";
            AppearanceBackgroundImageEnabled = true;
            _settingsStore.Update(settings =>
            {
                settings.AppearanceSidebarBackgroundImagePath = targetPath;
                settings.AppearanceBackgroundImageEnabled = true;
                settings.AppearanceBackgroundMode = "split";
            });
            SetAppearanceStatus("Status.AppearanceSidebarBackgroundPreviewApplied", targetPath);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to apply sidebar appearance background preview.", exception);
            SetAppearanceStatus("Status.AppearanceBackgroundFailed", exception.Message);
        }
    }

    private void CancelAppearanceSidebarBackgroundPreview()
    {
        if (!HasPendingAppearanceSidebarBackground)
        {
            return;
        }

        var pendingPath = _pendingAppearanceSidebarBackgroundImagePath;
        SetPendingAppearanceSidebarBackgroundImagePath(string.Empty);
        DeleteFileIfExists(pendingPath);
        SetAppearanceStatus("Status.AppearanceSidebarBackgroundPreviewCancelled");
    }

    private void ApplyAppearanceContentBackgroundPreview()
    {
        if (!HasPendingAppearanceContentBackground)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_appearanceAssetsDirectory);
            var targetPath = Path.Combine(_appearanceAssetsDirectory, "content-background.png");
            var pendingPath = _pendingAppearanceContentBackgroundImagePath;
            CopyFileUnlessSamePath(pendingPath, targetPath);

            AppearanceContentBackgroundImagePath = targetPath;
            SetPendingAppearanceContentBackgroundImagePath(string.Empty);
            DeleteFileIfExists(pendingPath);
            SelectedAppearanceBackgroundMode = "split";
            AppearanceBackgroundImageEnabled = true;
            _settingsStore.Update(settings =>
            {
                settings.AppearanceContentBackgroundImagePath = targetPath;
                settings.AppearanceBackgroundImageEnabled = true;
                settings.AppearanceBackgroundMode = "split";
            });
            SetAppearanceStatus("Status.AppearanceContentBackgroundPreviewApplied", targetPath);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to apply content appearance background preview.", exception);
            SetAppearanceStatus("Status.AppearanceBackgroundFailed", exception.Message);
        }
    }

    private void CancelAppearanceContentBackgroundPreview()
    {
        if (!HasPendingAppearanceContentBackground)
        {
            return;
        }

        var pendingPath = _pendingAppearanceContentBackgroundImagePath;
        SetPendingAppearanceContentBackgroundImagePath(string.Empty);
        DeleteFileIfExists(pendingPath);
        SetAppearanceStatus("Status.AppearanceContentBackgroundPreviewCancelled");
    }

    private void ClearAppearanceBackgroundImage()
    {
        AppearanceBackgroundImagePath = string.Empty;
        AppearanceBackgroundImageEnabled = false;
        _settingsStore.Update(settings =>
        {
            settings.AppearanceBackgroundImagePath = null;
            settings.AppearanceBackgroundImageEnabled = false;
        });
        SetAppearanceStatus("Status.AppearanceBackgroundCleared");
    }

    private void ClearAppearanceRegionBackgroundImages()
    {
        AppearanceSidebarBackgroundImagePath = string.Empty;
        AppearanceContentBackgroundImagePath = string.Empty;
        SetPendingAppearanceSidebarBackgroundImagePath(string.Empty);
        SetPendingAppearanceContentBackgroundImagePath(string.Empty);
        _settingsStore.Update(settings =>
        {
            settings.AppearanceSidebarBackgroundImagePath = null;
            settings.AppearanceContentBackgroundImagePath = null;
        });
        SetAppearanceStatus("Status.AppearanceRegionBackgroundCleared");
    }


    private string GetAppearanceThemeDisplayName(string themeKey) =>
        T(GetAppearanceTheme(themeKey).DisplayNameKey);

    private static AppearanceThemeDefinition GetAppearanceTheme(string themeKey) =>
        AppearanceThemes.FirstOrDefault(theme => string.Equals(theme.Key, themeKey, StringComparison.OrdinalIgnoreCase)) ??
        AppearanceThemes[0];

    private void ApplyAppearanceResources(bool animate = true)
    {
        var theme = GetAppearanceTheme(SelectedAppearanceThemeKey);
        var surfaceColor = TryParseAppearanceColor(AppearanceSurfaceColorHex, out var customSurfaceColor)
            ? customSurfaceColor
            : Colors.White;
        var borderColor = TryParseAppearanceColor(AppearanceBorderColorHex, out var customBorderColor)
            ? customBorderColor
            : theme.Border;
        UpdateBrushResource("AccentBrush", theme.Accent, animate);
        UpdateBrushResource("HighlightBrush", theme.Accent, animate);
        UpdateBrushResource("AccentSoftBrush", theme.AccentSoft, animate);
        UpdateBrushResource("CardBorderBrush", borderColor, animate);
        UpdateBrushResource("SidebarSelectedBrush", theme.AccentSoft, animate);
        UpdateBrushResource("SidebarBackgroundBrush", theme.Sidebar, animate);
        UpdateBrushResource("ScrollBarThumbBrush", WithAlpha(theme.Accent, 0x8A), animate);
        UpdateBrushResource("ScrollBarThumbHoverBrush", WithAlpha(theme.Accent, 0xBF), animate);
        UpdateBrushResource("ScrollBarThumbPressedBrush", WithAlpha(theme.Accent, 0xE6), animate);
        UpdateBrushResource("CardBackgroundBrush", WithAlpha(surfaceColor, ToByte(AppearanceCardOpacity)), animate);
        UpdateBrushResource("SidebarSurfaceBrush", WithAlpha(surfaceColor, ToByte(AppearanceCardOpacity)), animate);

        AppearanceWindowOverlayBrush = new SolidColorBrush(WithAlpha(Colors.White, ToByte(AppearanceWindowOverlayOpacity)));
        AppearancePreviewOverlayBrush = new SolidColorBrush(WithAlpha(Colors.White, ToByte(Math.Clamp(AppearanceWindowOverlayOpacity, 0.0, 0.94))));
        AppearanceSidebarOverlayBrush = new SolidColorBrush(WithAlpha(theme.Sidebar, ToByte(Math.Clamp(AppearanceWindowOverlayOpacity, 0.0, 0.98))));
        AppearanceContentOverlayBrush = new SolidColorBrush(WithAlpha(Color.FromRgb(0xF8, 0xFA, 0xFD), ToByte(Math.Clamp(AppearanceWindowOverlayOpacity, 0.0, 0.98))));
    }

    private void NotifyAppearanceBackgroundPropertiesChanged()
    {
        OnPropertyChanged(nameof(AppearanceBackgroundImageVisibility));
        OnPropertyChanged(nameof(AppearanceSidebarBackgroundImageVisibility));
        OnPropertyChanged(nameof(AppearanceSidebarBackgroundPreviewPath));
        OnPropertyChanged(nameof(AppearanceSidebarBackgroundPreviewVisibility));
        OnPropertyChanged(nameof(AppearanceSidebarAdjustmentVisibility));
        OnPropertyChanged(nameof(HasPendingAppearanceSidebarBackground));
        OnPropertyChanged(nameof(AppearanceContentBackgroundImageVisibility));
        OnPropertyChanged(nameof(AppearanceContentBackgroundPreviewPath));
        OnPropertyChanged(nameof(AppearanceContentBackgroundPreviewVisibility));
        OnPropertyChanged(nameof(AppearanceContentAdjustmentVisibility));
        OnPropertyChanged(nameof(HasPendingAppearanceContentBackground));
        OnPropertyChanged(nameof(AppearanceBackgroundImageDisplayPath));
        OnPropertyChanged(nameof(AppearanceSplitBackgroundSummary));

        if (ClearAppearanceBackgroundCommand is RelayCommand clearUnifiedCommand)
        {
            clearUnifiedCommand.RaiseCanExecuteChanged();
        }

        if (ClearAppearanceRegionBackgroundsCommand is RelayCommand clearRegionCommand)
        {
            clearRegionCommand.RaiseCanExecuteChanged();
        }

        if (ApplyAppearanceSidebarBackgroundPreviewCommand is RelayCommand applySidebarPreviewCommand)
        {
            applySidebarPreviewCommand.RaiseCanExecuteChanged();
        }

        if (CancelAppearanceSidebarBackgroundPreviewCommand is RelayCommand cancelSidebarPreviewCommand)
        {
            cancelSidebarPreviewCommand.RaiseCanExecuteChanged();
        }

        if (ApplyAppearanceContentBackgroundPreviewCommand is RelayCommand applyContentPreviewCommand)
        {
            applyContentPreviewCommand.RaiseCanExecuteChanged();
        }

        if (CancelAppearanceContentBackgroundPreviewCommand is RelayCommand cancelContentPreviewCommand)
        {
            cancelContentPreviewCommand.RaiseCanExecuteChanged();
        }

    }

    private void SetPendingAppearanceSidebarBackgroundImagePath(string path)
    {
        if (SetProperty(ref _pendingAppearanceSidebarBackgroundImagePath, path, nameof(AppearanceSidebarBackgroundPreviewPath)))
        {
            NotifyAppearanceBackgroundPropertiesChanged();
        }
    }

    private void SetPendingAppearanceContentBackgroundImagePath(string path)
    {
        if (SetProperty(ref _pendingAppearanceContentBackgroundImagePath, path, nameof(AppearanceContentBackgroundPreviewPath)))
        {
            NotifyAppearanceBackgroundPropertiesChanged();
        }
    }

    private static void CopyFileUnlessSamePath(string sourcePath, string destinationPath)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationFullPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        File.Copy(sourceFullPath, destinationFullPath, overwrite: true);
    }

    private void DeleteFileIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            _logger.Warn($"Failed to delete temporary appearance file {path}: {exception.Message}");
        }
    }

    private void UpdateBrushResource(string key, Color color, bool animate) =>
        BrushTransitionService.UpdateResource(
            key,
            color,
            animate && UiMotionEnabled,
            UiMotionDurationMilliseconds);

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static byte ToByte(double opacity) =>
        (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);

    private static bool TryParseAppearanceColor(string? colorHex, out Color color)
    {
        color = Colors.White;
        var normalized = JsonAppSettingsStore.NormalizeAppearanceColorHex(colorHex);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        color = Color.FromRgb(
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
        return true;
    }

}
