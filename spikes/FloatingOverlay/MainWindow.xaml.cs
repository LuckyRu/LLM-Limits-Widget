using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace LLMLimitsWidget.FloatingOverlay;

public partial class MainWindow : Window
{
    private readonly WidgetAppearance _appearance = new();
    private readonly WidgetSettings _settings;
    private readonly WindowPlacementController _placementController;
    private readonly GhostModeController _ghostModeController;
    private readonly OverlayZOrderSupervisor _zOrderSupervisor;
    private readonly bool _suppressPersistedGhost;
    private const double MinimumScale = 0.6;
    private const double MaximumScale = 2.0;
    private const double ResizeHandleSize = 24;
    private double _scale = 1.0;
    private bool _isResizing;
    private ResizeCorner _resizeCorner;
    private System.Windows.Point _resizeStartScreen;
    private double _resizeStartScale;
    private PixelRect _resizeStartRect;
    private uint _resizeStartDpi = 96;
    private bool _isLoaded;
    private bool _recoveryChannelAvailable;
    private bool _ghostPreference;
    private bool _widgetContextMenuDemoted;
    private bool _widgetContextMenuFallback;

    public MainWindow(bool suppressPersistedGhost = false)
    {
        _settings = WidgetSettingsStore.Load();
        _ghostPreference = _settings.GhostModeEnabled;
        _suppressPersistedGhost = suppressPersistedGhost;
        _appearance.Orientation = _settings.Orientation;
        _appearance.Scale = _settings.Scale;
        _scale = _settings.Scale;
        _appearance.SurfaceOpacity = _settings.SurfaceOpacity;
        _appearance.CornerRadius = _settings.CornerRadius;
        InitializeComponent();
        _placementController = new WindowPlacementController(this);
        _ghostModeController = new GhostModeController(this, Root);
        _zOrderSupervisor = new OverlayZOrderSupervisor(_placementController, Dispatcher);
        _zOrderSupervisor.TopmostHealthChanged += ZOrderSupervisor_TopmostHealthChanged;
        _placementController.PlacementCommitted += PlacementController_PlacementCommitted;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetOrientation(_appearance.Orientation, persist: false);
        ApplySurfaceBackgroundOpacity();
        Surface.CornerRadius = new CornerRadius(_appearance.CornerRadius);

        _isLoaded = true;
        UpdateLayout();
        if (_settings.Placement?.IsValid == true)
        {
            _placementController.Restore(_settings.Placement);
        }
        else
        {
            _placementController.PlaceAtDefault();
        }
        _zOrderSupervisor.SetVisible(true);
        if (GhostStartupPolicy.ShouldRestore(
                _ghostPreference,
                _suppressPersistedGhost,
                _recoveryChannelAvailable))
        {
            LastGhostModeResult = ApplyGhostModeTransition(true);
        }
        PersistSettings();
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _zOrderSupervisor.SetVisible(IsVisible);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _placementController.Attach();
        _ghostModeController.Attach();
        HooksAvailable = _zOrderSupervisor.Attach();
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            try
            {
                DragMove();
            }
            finally
            {
                _placementController.NormalizeCurrentWindow(snapToEdges: true);
                PersistSettings();
            }
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
        _resizeStartRect = _placementController.GetCurrentWindowRect()
            ?? new PixelRect(0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight));
        _resizeStartDpi = _placementController.GetCurrentDpi();
        CaptureMouse();
        e.Handled = true;
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FinishResize())
        {
            e.Handled = true;
        }
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

        var baseWidthPixels = _appearance.BaseWidth * _resizeStartDpi / 96d;
        var nextScale = Math.Clamp(_resizeStartScale + (signedDelta / baseWidthPixels), MinimumScale, MaximumScale);
        ApplyScale(nextScale, keepCenter: false);
        AnchorAfterResize();
        e.Handled = true;
    }

    private void Window_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        FinishResize();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        FinishResize();
        _zOrderSupervisor.Reassert("window-deactivated");
    }

    private bool FinishResize()
    {
        if (!_isResizing)
        {
            return false;
        }

        _isResizing = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        UpdateLayout();
        _placementController.NormalizeCurrentWindow(snapToEdges: true);
        PersistSettings();
        return true;
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
        UpdateLayout();
        _placementController.NormalizeCurrentWindow(snapToEdges: true);
        PersistSettings();
    }

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RefreshSample();
    }

    private void SurfaceOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoaded)
        {
            _appearance.SurfaceOpacity = e.NewValue;
        }

        if (sender is System.Windows.Controls.Slider slider)
        {
            slider.ToolTip = $"Прозрачность фона: {e.NewValue:P0}";
        }

        if (Surface is not null)
        {
            ApplySurfaceBackgroundOpacity();
        }

        PersistSettings();
    }

    private void WidgetContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _widgetContextMenuDemoted |= _zOrderSupervisor.SetMenuOpen(true);
        var menuRaised = false;
        if (sender is System.Windows.Controls.ContextMenu menu
            && System.Windows.PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource source)
        {
            menuRaised = ManagementMenuZOrder.EnsureAboveOverlay(source.Handle);
        }
        if (!_widgetContextMenuDemoted && !menuRaised)
        {
            ReportManagementMenuFailure();
            _widgetContextMenuFallback = true;
            _ghostModeController.SetManagementBypass(true);
            Surface.Visibility = Visibility.Hidden;
        }
        if (FindVisualChild<System.Windows.Controls.Slider>(sender as DependencyObject) is { } slider)
        {
            slider.Value = _appearance.SurfaceOpacity;
            slider.ToolTip = $"Прозрачность фона: {_appearance.SurfaceOpacity:P0}";
        }
    }

    private void Surface_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        _widgetContextMenuDemoted = _zOrderSupervisor.SetMenuOpen(true);
    }

    private void WidgetContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (_widgetContextMenuFallback)
        {
            _ghostModeController.SetManagementBypass(false);
            Surface.Visibility = Visibility.Visible;
            _widgetContextMenuFallback = false;
        }
        _zOrderSupervisor.SetMenuOpen(false);
        _widgetContextMenuDemoted = false;
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
        PersistSettings();
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    public bool IsGhostModeEnabled => _ghostModeController.IsEnabled;

    internal bool GhostCleanupRequired => _ghostModeController.RequiresCleanup;

    internal bool IsGhostInputSuppressed => _ghostModeController.IsInputSuppressed;

    internal GhostModeTransitionResult LastGhostModeResult { get; private set; } = GhostModeTransitionResult.Success;

    public bool HooksAvailable { get; private set; }

    internal event EventHandler<bool>? GhostModeChanged;

    internal GhostModeTransitionResult SetGhostMode(bool enabled, IntPtr foregroundToRestore = default)
    {
        if (enabled && !_recoveryChannelAvailable)
        {
            LastGhostModeResult = GhostModeTransitionResult.HandleUnavailable;
            return LastGhostModeResult;
        }

        LastGhostModeResult = ApplyGhostModeTransition(enabled, foregroundToRestore);
        if (LastGhostModeResult is not (GhostModeTransitionResult.Success
            or GhostModeTransitionResult.AlreadyInRequestedState))
        {
            return LastGhostModeResult;
        }

        _ghostPreference = enabled;
        PersistSettings();
        _zOrderSupervisor.Reassert(enabled ? "ghost-enabled" : "ghost-disabled");
        GhostModeChanged?.Invoke(this, enabled);
        return LastGhostModeResult;
    }

    private GhostModeTransitionResult ApplyGhostModeTransition(
        bool enabled,
        IntPtr foregroundToRestore = default)
    {
        var result = _ghostModeController.SetEnabled(enabled, foregroundToRestore);
        if (!enabled
            || result is not (GhostModeTransitionResult.Success
                or GhostModeTransitionResult.AlreadyInRequestedState))
        {
            return result;
        }

        if (_zOrderSupervisor.EnsureTopmostNow())
        {
            return result;
        }

        var rollback = _ghostModeController.SetEnabled(false);
        return GhostTransitionPolicy.ResolveTopmostFailure(rollback);
    }

    internal void SetRecoveryChannelAvailable(bool available)
    {
        _recoveryChannelAvailable = available;
    }

    internal bool SetTrayMenuOpen(bool open)
    {
        return _zOrderSupervisor.SetMenuOpen(open);
    }

    internal void ReportManagementMenuFailure()
    {
        LastGhostModeResult = GhostModeTransitionResult.ManagementMenuUnavailable;
        GhostModeChanged?.Invoke(this, IsGhostModeEnabled);
    }

    private void ZOrderSupervisor_TopmostHealthChanged(object? sender, bool healthy)
    {
        if (!_ghostModeController.IsEnabled)
        {
            return;
        }

        if (!healthy)
        {
            LastGhostModeResult = GhostModeTransitionResult.TopmostUnavailable;
            GhostModeChanged?.Invoke(this, true);
        }
        else if (LastGhostModeResult == GhostModeTransitionResult.TopmostUnavailable)
        {
            LastGhostModeResult = GhostModeTransitionResult.Success;
            GhostModeChanged?.Invoke(this, true);
        }
    }

    private void ResetPosition()
    {
        _placementController.PlaceAtDefault();
    }

    public void EnsureVisible()
    {
        LastGhostModeResult = _ghostModeController.EnsureApplied();
        UpdateLayout();
        _placementController.NormalizeCurrentWindow(snapToEdges: false);
        PersistSettings();
    }

    public void ResetWidgetPosition()
    {
        ResetPosition();
        PersistSettings();
    }

    private void RefreshSample()
    {
        // Provider adapters will replace the mock values after the visual spike.
        ToolTip = $"Mock refreshed {DateTime.Now:HH:mm:ss}";
    }

    private void SetOrientation(LayoutOrientation orientation)
    {
        SetOrientation(orientation, persist: true);
    }

    private void SetOrientation(LayoutOrientation orientation, bool persist)
    {
        var previousPlacement = _isLoaded ? _placementController.Capture() : null;
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
        if (_isLoaded)
        {
            UpdateLayout();
            _placementController.Restore(previousPlacement);
        }
        if (persist)
        {
            PersistSettings();
        }
    }

    private void ApplyScale(double scale, bool keepCenter)
    {
        var previousPlacement = keepCenter && _isLoaded ? _placementController.Capture() : null;
        _scale = Math.Clamp(scale, MinimumScale, MaximumScale);
        _appearance.Scale = _scale;
        Width = _appearance.BaseWidth * _scale;
        Height = _appearance.BaseHeight * _scale;

        if (keepCenter && _isLoaded)
        {
            UpdateLayout();
            _placementController.Restore(previousPlacement);
        }
    }

    private void AnchorAfterResize()
    {
        UpdateLayout();
        _placementController.AnchorResize(
            _resizeStartRect,
            anchorRight: _resizeCorner is ResizeCorner.TopLeft or ResizeCorner.BottomLeft,
            anchorBottom: _resizeCorner is ResizeCorner.TopLeft or ResizeCorner.TopRight);
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
        if (_ghostModeController.IsInputSuppressed || _widgetContextMenuFallback)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        _zOrderSupervisor.SetVisible(false);
        PersistSettings();
        _zOrderSupervisor.TopmostHealthChanged -= ZOrderSupervisor_TopmostHealthChanged;
        _zOrderSupervisor.Dispose();
        _ghostModeController.Dispose();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _placementController.PlacementCommitted -= PlacementController_PlacementCommitted;
        _placementController.Dispose();
    }

    private void PlacementController_PlacementCommitted(object? sender, EventArgs e)
    {
        PersistSettings();
    }

    private void PersistSettings()
    {
        if (!_isLoaded)
        {
            return;
        }

        _settings.Orientation = _appearance.Orientation;
        _settings.Scale = _appearance.Scale;
        _settings.SurfaceOpacity = _appearance.SurfaceOpacity;
        _settings.CornerRadius = _appearance.CornerRadius;
        _settings.GhostModeEnabled = _ghostPreference;
        if (_placementController.Capture() is { } placement)
        {
            _settings.Placement = placement;
        }
        WidgetSettingsStore.Save(_settings);
    }

    private static T? FindVisualChild<T>(DependencyObject? parent)
        where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
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

internal static class GhostTransitionPolicy
{
    public static GhostModeTransitionResult ResolveTopmostFailure(
        GhostModeTransitionResult rollbackResult)
    {
        return rollbackResult is GhostModeTransitionResult.Success
            or GhostModeTransitionResult.AlreadyInRequestedState
            ? GhostModeTransitionResult.TopmostUnavailable
            : GhostModeTransitionResult.RollbackFailed;
    }
}
