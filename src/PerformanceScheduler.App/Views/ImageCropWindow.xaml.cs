using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerformanceScheduler.App.Views;

public partial class ImageCropWindow : Window
{
    private readonly string _outputPath;
    private readonly double _targetAspectRatio;
    private readonly BitmapImage _sourceBitmap;
    private Rect _imageBounds;
    private Rect _cropBounds;
    private bool _cropInitialized;
    private bool _isDraggingCrop;
    private Point _lastDragPoint;

    public ImageCropWindow(
        string sourcePath,
        string outputPath,
        double targetWidth,
        double targetHeight,
        string title,
        string instruction,
        string footer,
        string confirmText,
        string cancelText)
    {
        _outputPath = outputPath;
        _targetAspectRatio = Math.Max(0.05, targetWidth / Math.Max(1, targetHeight));
        _sourceBitmap = LoadBitmap(sourcePath);

        InitializeComponent();

        Title = title;
        WindowTitleText.Text = title;
        TitleText.Text = title;
        InstructionText.Text = instruction;
        FooterText.Text = footer;
        ConfirmButton.Content = confirmText;
        CancelCropButton.Content = cancelText;
        SourceImage.Source = _sourceBitmap;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void CropCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateImageLayout();
        if (!_cropInitialized)
        {
            InitializeCropFrame();
        }
        else
        {
            _cropBounds = ClampCropToImage(_cropBounds);
        }

        RenderCropFrame();
    }

    private void UpdateImageLayout()
    {
        var availableWidth = Math.Max(1, CropCanvas.ActualWidth);
        var availableHeight = Math.Max(1, CropCanvas.ActualHeight);
        var imageAspectRatio = _sourceBitmap.PixelWidth / (double)Math.Max(1, _sourceBitmap.PixelHeight);
        var canvasAspectRatio = availableWidth / availableHeight;

        double displayWidth;
        double displayHeight;
        if (imageAspectRatio > canvasAspectRatio)
        {
            displayWidth = availableWidth;
            displayHeight = displayWidth / imageAspectRatio;
        }
        else
        {
            displayHeight = availableHeight;
            displayWidth = displayHeight * imageAspectRatio;
        }

        var left = (availableWidth - displayWidth) / 2;
        var top = (availableHeight - displayHeight) / 2;
        _imageBounds = new Rect(left, top, displayWidth, displayHeight);

        SourceImage.Width = displayWidth;
        SourceImage.Height = displayHeight;
        Canvas.SetLeft(SourceImage, left);
        Canvas.SetTop(SourceImage, top);
    }

    private void InitializeCropFrame()
    {
        var width = _imageBounds.Width * 0.62;
        var height = width / _targetAspectRatio;
        if (height > _imageBounds.Height * 0.88)
        {
            height = _imageBounds.Height * 0.88;
            width = height * _targetAspectRatio;
        }

        _cropBounds = new Rect(
            _imageBounds.Left + (_imageBounds.Width - width) / 2,
            _imageBounds.Top + (_imageBounds.Height - height) / 2,
            width,
            height);
        _cropInitialized = true;
    }

    private void CropFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingCrop = true;
        _lastDragPoint = e.GetPosition(CropCanvas);
        CropFrame.CaptureMouse();
        e.Handled = true;
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingCrop)
        {
            return;
        }

        var point = e.GetPosition(CropCanvas);
        var delta = point - _lastDragPoint;
        _lastDragPoint = point;
        _cropBounds.Offset(delta.X, delta.Y);
        _cropBounds = ClampCropToImage(_cropBounds);
        RenderCropFrame();
    }

    private void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndCropDrag();
    }

    private void Handle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not string handle)
        {
            return;
        }

        var minWidth = Math.Min(96, Math.Max(36, _imageBounds.Width * 0.16));
        var maxWidth = _imageBounds.Width;
        var width = _cropBounds.Width;

        if (handle is "BottomRight" or "TopRight")
        {
            width += e.HorizontalChange;
        }
        else
        {
            width -= e.HorizontalChange;
        }

        if (handle is "BottomLeft" or "BottomRight")
        {
            width += e.VerticalChange * _targetAspectRatio;
        }
        else
        {
            width -= e.VerticalChange * _targetAspectRatio;
        }

        width = Math.Clamp(width, minWidth, maxWidth);
        var height = width / _targetAspectRatio;
        if (height > _imageBounds.Height)
        {
            height = _imageBounds.Height;
            width = height * _targetAspectRatio;
        }

        var left = handle is "TopLeft" or "BottomLeft" ? _cropBounds.Right - width : _cropBounds.Left;
        var top = handle is "TopLeft" or "TopRight" ? _cropBounds.Bottom - height : _cropBounds.Top;
        _cropBounds = ClampCropToImage(new Rect(left, top, width, height));
        RenderCropFrame();
    }

    private Rect ClampCropToImage(Rect bounds)
    {
        var width = Math.Min(bounds.Width, _imageBounds.Width);
        var height = Math.Min(bounds.Height, _imageBounds.Height);
        var left = Math.Clamp(bounds.Left, _imageBounds.Left, _imageBounds.Right - width);
        var top = Math.Clamp(bounds.Top, _imageBounds.Top, _imageBounds.Bottom - height);
        return new Rect(left, top, width, height);
    }

    private void RenderCropFrame()
    {
        if (_imageBounds.Width <= 0 || _cropBounds.Width <= 0)
        {
            return;
        }

        Canvas.SetLeft(CropFrame, _cropBounds.Left);
        Canvas.SetTop(CropFrame, _cropBounds.Top);
        CropFrame.Width = _cropBounds.Width;
        CropFrame.Height = _cropBounds.Height;

        PositionHandle(TopLeftHandle, _cropBounds.Left, _cropBounds.Top);
        PositionHandle(TopRightHandle, _cropBounds.Right, _cropBounds.Top);
        PositionHandle(BottomLeftHandle, _cropBounds.Left, _cropBounds.Bottom);
        PositionHandle(BottomRightHandle, _cropBounds.Right, _cropBounds.Bottom);

        var full = new RectangleGeometry(new Rect(0, 0, CropCanvas.ActualWidth, CropCanvas.ActualHeight));
        var crop = new RectangleGeometry(_cropBounds);
        OverlayPath.Data = Geometry.Combine(full, crop, GeometryCombineMode.Exclude, null);
    }

    private static void PositionHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - handle.Width / 2);
        Canvas.SetTop(handle, y - handle.Height / 2);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCroppedImage();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelCropButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SaveCroppedImage()
    {
        var sourceRect = GetSourceCropRect();
        var cropped = new CroppedBitmap(_sourceBitmap, sourceRect);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));

        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var output = File.Create(_outputPath);
        encoder.Save(output);
    }

    private Int32Rect GetSourceCropRect()
    {
        var scaleX = _sourceBitmap.PixelWidth / _imageBounds.Width;
        var scaleY = _sourceBitmap.PixelHeight / _imageBounds.Height;
        var x = (int)Math.Round((_cropBounds.Left - _imageBounds.Left) * scaleX);
        var y = (int)Math.Round((_cropBounds.Top - _imageBounds.Top) * scaleY);
        var width = (int)Math.Round(_cropBounds.Width * scaleX);
        var height = (int)Math.Round(_cropBounds.Height * scaleY);

        x = Math.Clamp(x, 0, Math.Max(0, _sourceBitmap.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, _sourceBitmap.PixelHeight - 1));
        width = Math.Clamp(width, 1, _sourceBitmap.PixelWidth - x);
        height = Math.Clamp(height, 1, _sourceBitmap.PixelHeight - y);
        return new Int32Rect(x, y, width, height);
    }

    private void EndCropDrag()
    {
        if (!_isDraggingCrop)
        {
            return;
        }

        _isDraggingCrop = false;
        CropFrame.ReleaseMouseCapture();
    }
}
