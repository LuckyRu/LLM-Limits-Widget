using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace LLMLimitsWidget.FloatingOverlay;

public partial class MainWindow : Window
{
    private readonly WidgetAppearance _appearance = new();
    private const double MinimumScale = 0.6;
    private const double MaximumScale = 2.0;
    private const double ResizeHandleSize = 24;
    private double _scale = 1.0;
    private bool _isResizing;
    private ResizeCorner _resizeCorner;
    private System.Windows.Point _resizeStartScreen;
    private double _resizeStartScale;
    private double _resizeStartLeft;
    private double _resizeStartTop;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySurfaceBackgroundOpacity();
        Surface.CornerRadius = new CornerRadius(_appearance.CornerRadius);
        ResetPosition();
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var corner = GetResizeCorner(e.GetPosition(this));
        if (corner == ResizeCorner.None)
        {
            return;
        }

        _isResizing = true;
        _resizeCorner = corner;
        _resizeStartScreen = PointToScreen(e.GetPosition(this));
        _resizeStartScale = _scale;
        _resizeStartLeft = Left;
        _resizeStartTop = Top;
        CaptureMouse();
        e.Handled = true;
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        _isResizing = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (!_isResizing)
        {
            Cursor = GetResizeCorner(position) switch
            {
                ResizeCorner.TopLeft or ResizeCorner.BottomRight => System.Windows.Input.Cursors.SizeNWSE,
                ResizeCorner.TopRight or ResizeCorner.BottomLeft => System.Windows.Input.Cursors.SizeNESW,
                _ => System.Windows.Input.Cursors.Arrow
            };
            return;
        }

        var currentScreen = PointToScreen(position);
        var deltaX = currentScreen.X - _resizeStartScreen.X;
        var deltaY = currentScreen.Y - _resizeStartScreen.Y;
        var signedDelta = _resizeCorner switch
        {
            ResizeCorner.TopLeft => (-deltaX - deltaY) / 2,
            ResizeCorner.TopRight => (deltaX - deltaY) / 2,
            ResizeCorner.BottomLeft => (-deltaX + deltaY) / 2,
            ResizeCorner.BottomRight => (deltaX + deltaY) / 2,
            _ => 0
        };

        var nextScale = Math.Clamp(_resizeStartScale + (signedDelta / _appearance.BaseWidth), MinimumScale, MaximumScale);
        ApplyScale(nextScale, keepCenter: false);
        AnchorAfterResize();
        e.Handled = true;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSample();
    }

    private void ScaleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }
            || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
        {
            return;
        }

        ApplyScale(scale, keepCenter: true);
    }

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RefreshSample();
    }

    private void SurfaceOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _appearance.SurfaceOpacity = e.NewValue;

        if (sender is System.Windows.Controls.Slider slider)
        {
            slider.ToolTip = $"Прозрачность фона: {e.NewValue:P0}";
        }

        if (Surface is not null)
        {
            ApplySurfaceBackgroundOpacity();
        }
    }

    private void ApplySurfaceBackgroundOpacity()
    {
        if (FindResource("SurfaceBrush") is not System.Windows.Media.SolidColorBrush surfaceBrush
            || FindResource("SurfaceBorderBrush") is not System.Windows.Media.SolidColorBrush borderBrush)
        {
            return;
        }

        Surface.Background = WithOpacity(surfaceBrush, _appearance.SurfaceOpacity);
        Surface.BorderBrush = WithOpacity(borderBrush, _appearance.SurfaceOpacity);
    }

    private static System.Windows.Media.SolidColorBrush WithOpacity(
        System.Windows.Media.SolidColorBrush sourceBrush,
        double opacity)
    {
        var color = sourceBrush.Color;
        var alpha = (byte)Math.Round(color.A * opacity);
        return new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private void VerticalLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetOrientation(LayoutOrientation.Vertical);
    }

    private void HorizontalLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetOrientation(LayoutOrientation.Horizontal);
    }

    private void ResetPositionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ResetPosition();
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    private void ResetPosition()
    {
        Left = SystemParameters.PrimaryScreenWidth - Width - 22;
        Top = SystemParameters.PrimaryScreenHeight - Height - 4;
    }

    private void RefreshSample()
    {
        // Provider adapters will replace the mock values after the visual spike.
        ToolTip = $"Mock refreshed {DateTime.Now:HH:mm:ss}";
    }

    private void SetOrientation(LayoutOrientation orientation)
    {
        var center = new System.Windows.Point(Left + (Width / 2), Top + (Height / 2));
        _appearance.Orientation = orientation;
        VerticalLayout.Visibility = orientation == LayoutOrientation.Vertical ? Visibility.Visible : Visibility.Collapsed;
        HorizontalLayout.Visibility = orientation == LayoutOrientation.Horizontal ? Visibility.Visible : Visibility.Collapsed;
        if (orientation == LayoutOrientation.Horizontal)
        {
            HorizontalLayout.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            _appearance.HorizontalWidth = HorizontalLayout.DesiredSize.Width + 20;
        }
        Surface.Width = _appearance.BaseWidth;
        Surface.Height = _appearance.BaseHeight;
        Width = _appearance.BaseWidth * _appearance.Scale;
        Height = _appearance.BaseHeight * _appearance.Scale;
        Left = center.X - (Width / 2);
        Top = center.Y - (Height / 2);
    }

    private void ApplyScale(double scale, bool keepCenter)
    {
        var center = new System.Windows.Point(Left + (Width / 2), Top + (Height / 2));
        _scale = Math.Clamp(scale, MinimumScale, MaximumScale);
        _appearance.Scale = _scale;
        Width = _appearance.BaseWidth * _scale;
        Height = _appearance.BaseHeight * _scale;

        if (keepCenter)
        {
            Left = center.X - (Width / 2);
            Top = center.Y - (Height / 2);
        }
    }

    private void AnchorAfterResize()
    {
        Left = _resizeCorner switch
        {
            ResizeCorner.TopLeft or ResizeCorner.BottomLeft => _resizeStartLeft + (_appearance.BaseWidth * _resizeStartScale) - Width,
            _ => _resizeStartLeft
        };
        Top = _resizeCorner switch
        {
            ResizeCorner.TopLeft or ResizeCorner.TopRight => _resizeStartTop + (_appearance.BaseHeight * _resizeStartScale) - Height,
            _ => _resizeStartTop
        };
    }

    private ResizeCorner GetResizeCorner(System.Windows.Point position)
    {
        var nearLeft = position.X <= ResizeHandleSize;
        var nearRight = position.X >= ActualWidth - ResizeHandleSize;
        var nearTop = position.Y <= ResizeHandleSize;
        var nearBottom = position.Y >= ActualHeight - ResizeHandleSize;

        return (nearLeft, nearTop, nearRight, nearBottom) switch
        {
            (true, true, _, _) => ResizeCorner.TopLeft,
            (_, true, true, _) => ResizeCorner.TopRight,
            (true, _, _, true) => ResizeCorner.BottomLeft,
            (_, _, true, true) => ResizeCorner.BottomRight,
            _ => ResizeCorner.None
        };
    }


    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    private enum ResizeCorner
    {
        None,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
}
