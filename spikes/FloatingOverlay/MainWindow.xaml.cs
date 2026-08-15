using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LLMLimitsWidget.FloatingOverlay;

public partial class MainWindow : Window
{
    private readonly WidgetAppearance _appearance = new();
    private readonly WidgetSettings _settings;
    private readonly WindowPlacementController _placementController;
    private readonly GhostModeController _ghostModeController;
    private readonly OverlayZOrderSupervisor _zOrderSupervisor;
    private readonly LimitsCoordinator _limitsCoordinator;
    private readonly DispatcherTimer _countdownTimer;
    private readonly CountdownViewModel _codexCountdown = new();
    private readonly CountdownViewModel _claudeCountdown = new();
    private readonly bool _suppressPersistedGhost;
    private static readonly System.Windows.Media.Brush CriticalCountdownBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(255, 183, 77));
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
        _ghostPreference = suppressPersistedGhost ? false : _settings.GhostModeEnabled;
        _suppressPersistedGhost = suppressPersistedGhost;
        _appearance.Orientation = _settings.Orientation;
        _appearance.Scale = _settings.Scale;
        _scale = _settings.Scale;
        _appearance.SurfaceOpacity = _settings.SurfaceOpacity;
        _appearance.CornerRadius = _settings.CornerRadius;
        InitializeComponent();
        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            IsEnabled = false
        };
        _countdownTimer.Tick += CountdownTimer_Tick;
        _placementController = new WindowPlacementController(this);
        _ghostModeController = new GhostModeController(this, Root);
        _zOrderSupervisor = new OverlayZOrderSupervisor(_placementController, Dispatcher);
        _zOrderSupervisor.TopmostHealthChanged += ZOrderSupervisor_TopmostHealthChanged;
        _placementController.PlacementCommitted += PlacementController_PlacementCommitted;
        _limitsCoordinator = new LimitsCoordinator(
            new ILimitsDataSource[]
            {
                new ProviderSupervisor(new CodexAppServerLimitsDataSource()),
                new ProviderSupervisor(new ClaudeHybridLimitsDataSource())
            });
        _limitsCoordinator.SnapshotChanged += LimitsCoordinator_SnapshotChanged;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        WidgetLogger.Info(
            "Wpf",
            "window_constructed",
            ("orientation", _appearance.Orientation),
            ("scale", _scale),
            ("persistedGhost", _settings.GhostModeEnabled),
            ("safeStartup", suppressPersistedGhost));
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
        _limitsCoordinator.Start();
        WidgetLogger.Info(
            "Wpf",
            "window_loaded",
            ("orientation", _appearance.Orientation),
            ("scale", _scale),
            ("ghostMode", IsGhostModeEnabled));
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
        _ = RefreshLimitsAsync(force: true);
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
        _ = RefreshLimitsAsync(force: true);
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

    private async Task RefreshLimitsAsync(bool force = false)
    {
        try
        {
            WidgetLogger.Debug("Wpf", "refresh_requested", ("force", force));
            await _limitsCoordinator.RefreshAsync(force: force);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            WidgetLogger.Error("Wpf", "refresh_request_failed", exception, ("force", force));
        }
    }

    private void LimitsCoordinator_SnapshotChanged(LimitsSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    ApplyLimitsSnapshot(snapshot);
                }
                catch (Exception exception)
                {
                    WidgetLogger.Error("Wpf", "snapshot_render_failed", exception);
                }
            });
            return;
        }

        try
        {
            ApplyLimitsSnapshot(snapshot);
        }
        catch (Exception exception)
        {
            WidgetLogger.Error("Wpf", "snapshot_render_failed", exception);
        }
    }

    private void ApplyLimitsSnapshot(LimitsSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        ApplyProviderSnapshot(
            snapshot.TryGetProvider(LimitProviderId.Codex, out var codexSnapshot) ? codexSnapshot : null,
            CodexVerticalRow,
            CodexHorizontalRow,
            now);
        ApplyProviderSnapshot(
            snapshot.TryGetProvider(LimitProviderId.Claude, out var claude) ? claude : null,
            ClaudeVerticalRow,
            ClaudeHorizontalRow,
            now);
        UpdateCountdownPresentation(codexSnapshot, claude, now, force: true);
        RefreshContentSize();
        ToolTip = $"Лимиты обновлены: {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}";
        WidgetLogger.Debug(
            "Wpf",
            "snapshot_rendered",
            ("codexStatus", snapshot.TryGetProvider(LimitProviderId.Codex, out var codex) ? codex.Status : LimitDataStatus.Unavailable),
            ("claudeStatus", snapshot.TryGetProvider(LimitProviderId.Claude, out var claudeSnapshot) ? claudeSnapshot.Status : LimitDataStatus.Unavailable));
    }

    private void RefreshContentSize()
    {
        if (!_isLoaded || _appearance.Orientation != LayoutOrientation.Horizontal)
        {
            return;
        }

        // The horizontal layout is intentionally auto-sized. Its desired width
        // changes when real provider data replaces the neutral startup state
        // (reset times and labels are usually longer), so the outer Window must
        // be resized after every rendered snapshot.
        UpdateLayout();
        HorizontalLayout.Measure(new System.Windows.Size(
            double.PositiveInfinity,
            double.PositiveInfinity));

        var contentWidth = HorizontalLayout.DesiredSize.Width
            + Surface.Padding.Left
            + Surface.Padding.Right
            + Surface.BorderThickness.Left
            + Surface.BorderThickness.Right;
        var nextBaseWidth = Math.Max(1, contentWidth);
        if (Math.Abs(nextBaseWidth - _appearance.BaseWidth) < 0.5)
        {
            return;
        }

        var previousPlacement = _placementController.Capture();
        _appearance.HorizontalWidth = nextBaseWidth;
        Surface.Width = _appearance.BaseWidth;
        Surface.Height = _appearance.BaseHeight;
        Width = _appearance.BaseWidth * _appearance.Scale;
        Height = _appearance.BaseHeight * _appearance.Scale;
        UpdateLayout();
        _placementController.Restore(previousPlacement);

        WidgetLogger.Debug(
            "Wpf",
            "content_size_refreshed",
            ("orientation", _appearance.Orientation),
            ("width", Width),
            ("height", Height));
    }

    private static void ApplyProviderSnapshot(
        ProviderLimitsSnapshot? snapshot,
        ProviderRowControl verticalRow,
        ProviderRowControl horizontalRow,
        DateTimeOffset now)
    {
        var layout = GetProviderMetricLayout(snapshot);
        var countdown = CountdownFormatter.Format(layout.CountdownReset, now);
        var resetLabel = layout.ResetLabelReset is { } reset
            ? $"{reset.ToLocalTime():dd MMM} · {reset.ToLocalTime():HH:mm}"
            : "—";
        var urgency = CountdownFormatter.GetUrgency(layout.CountdownReset, now);

        ApplyMetric(verticalRow, layout.First, layout.Second, countdown, resetLabel, snapshot, urgency);
        ApplyMetric(horizontalRow, layout.First, layout.Second, countdown, resetLabel, snapshot, urgency);
    }

    private static ProviderMetricLayout GetProviderMetricLayout(ProviderLimitsSnapshot? snapshot)
    {
        var windows = snapshot?.Windows ?? Array.Empty<LimitWindowSnapshot>();
        var first = snapshot?.Provider == LimitProviderId.Claude
            ? windows.FirstOrDefault(window => window.Kind == LimitWindowKind.FiveHour)
                ?? windows.ElementAtOrDefault(0)
            : windows.FirstOrDefault(window => window.Kind == LimitWindowKind.Weekly)
                ?? windows.ElementAtOrDefault(0);
        var second = snapshot?.Provider == LimitProviderId.Claude
            ? windows.FirstOrDefault(window => window.Kind == LimitWindowKind.SevenDay)
            : null;
        var nearestReset = windows
            .Where(window => window.ResetAt.HasValue)
            .OrderBy(window => window.ResetAt)
            .FirstOrDefault();
        var fiveHourReset = first?.Kind == LimitWindowKind.FiveHour
            ? first.ResetAt
            : windows.FirstOrDefault(window => window.Kind == LimitWindowKind.FiveHour)?.ResetAt;
        var countdownReset = snapshot?.Provider == LimitProviderId.Claude
            ? fiveHourReset
            : nearestReset?.ResetAt;
        var resetLabelReset = snapshot?.Provider == LimitProviderId.Claude
            ? second?.ResetAt
            : countdownReset;
        return new ProviderMetricLayout(first, second, countdownReset, resetLabelReset);
    }

    private sealed record ProviderMetricLayout(
        LimitWindowSnapshot? First,
        LimitWindowSnapshot? Second,
        DateTimeOffset? CountdownReset,
        DateTimeOffset? ResetLabelReset);

    private static void ApplyMetric(
        ProviderRowControl row,
        LimitWindowSnapshot? first,
        LimitWindowSnapshot? second,
        string countdown,
        string resetLabel,
        ProviderLimitsSnapshot? snapshot,
        CountdownUrgency urgency)
    {
        row.MetricOneValue = first?.SafeRemainingPercent ?? 0;
        row.MetricOnePercent = FormatPercent(first?.SafeRemainingPercent);
        row.MetricOnePeriod = first?.Label ?? "—";
        row.MetricTwoValue = second?.SafeRemainingPercent ?? 0;
        row.MetricTwoPercent = FormatPercent(second?.SafeRemainingPercent);
        row.MetricTwoPeriod = second?.Label ?? string.Empty;
        row.HasSecondMetric = second is not null;
        row.Countdown = countdown;
        row.CountdownBrush = GetCountdownBrush(urgency, row.Accent);
        row.ResetLabel = resetLabel;
        row.ToolTip = snapshot?.ErrorMessage is { Length: > 0 } error
            ? $"{snapshot.Status}: {error}"
            : snapshot?.Status.ToString() ?? LimitDataStatus.Unavailable.ToString();
    }

    private static string FormatPercent(double? percent)
    {
        return percent.HasValue
            ? $"{percent.Value:0.##}%"
            : "—";
    }

    private static System.Windows.Media.Brush GetCountdownBrush(
        CountdownUrgency urgency,
        System.Windows.Media.Brush providerAccent)
    {
        return urgency switch
        {
            CountdownUrgency.Critical => CriticalCountdownBrush,
            CountdownUrgency.Near => providerAccent,
            _ => System.Windows.Media.Brushes.White
        };
    }

    private void UpdateCountdownPresentation(
        ProviderLimitsSnapshot? codexSnapshot,
        ProviderLimitsSnapshot? claudeSnapshot,
        DateTimeOffset now,
        bool force)
    {
        _ = _codexCountdown.Update(
            GetProviderMetricLayout(codexSnapshot).CountdownReset,
            now);
        _ = _claudeCountdown.Update(
            GetProviderMetricLayout(claudeSnapshot).CountdownReset,
            now);
        if (force)
        {
            ApplyCountdown(CodexVerticalRow, _codexCountdown);
            ApplyCountdown(CodexHorizontalRow, _codexCountdown);
        }
        if (force)
        {
            ApplyCountdown(ClaudeVerticalRow, _claudeCountdown);
            ApplyCountdown(ClaudeHorizontalRow, _claudeCountdown);
        }

        ScheduleNextCountdownRender(now);
    }

    private void RefreshCountdownPresentation(DateTimeOffset now)
    {
        var codexChanged = _codexCountdown.Update(_codexCountdown.ResetAt, now);
        var claudeChanged = _claudeCountdown.Update(_claudeCountdown.ResetAt, now);
        if (codexChanged)
        {
            ApplyCountdown(CodexVerticalRow, _codexCountdown);
            ApplyCountdown(CodexHorizontalRow, _codexCountdown);
        }
        if (claudeChanged)
        {
            ApplyCountdown(ClaudeVerticalRow, _claudeCountdown);
            ApplyCountdown(ClaudeHorizontalRow, _claudeCountdown);
        }

        ScheduleNextCountdownRender(now);
    }

    private static void ApplyCountdown(ProviderRowControl row, CountdownViewModel viewModel)
    {
        if (!string.Equals(row.Countdown, viewModel.Text, StringComparison.Ordinal))
        {
            row.Countdown = viewModel.Text;
        }

        var brush = GetCountdownBrush(viewModel.Urgency, row.Accent);
        if (!ReferenceEquals(row.CountdownBrush, brush))
        {
            row.CountdownBrush = brush;
        }
    }

    private void ScheduleNextCountdownRender(DateTimeOffset now)
    {
        var next = new[]
            {
                _codexCountdown.GetNextVisualChangeAt(now),
                _claudeCountdown.GetNextVisualChangeAt(now)
            }
            .Where(candidate => candidate.HasValue)
            .Select(candidate => candidate!.Value)
            .DefaultIfEmpty()
            .Min();
        if (next == default)
        {
            _countdownTimer.Stop();
            return;
        }

        var delay = next - now;
        _countdownTimer.Interval = delay > TimeSpan.FromMilliseconds(50)
            ? delay
            : TimeSpan.FromMilliseconds(50);
        _countdownTimer.Start();
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _countdownTimer.Stop();
        if (!_isLoaded)
        {
            return;
        }

        RefreshCountdownPresentation(DateTimeOffset.Now);
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
        WidgetLogger.Info("Wpf", "window_closing", ("cancelled", e.Cancel));
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        _zOrderSupervisor.SetVisible(false);
        _countdownTimer.Stop();
        _countdownTimer.Tick -= CountdownTimer_Tick;
        PersistSettings();
        _zOrderSupervisor.TopmostHealthChanged -= ZOrderSupervisor_TopmostHealthChanged;
        _zOrderSupervisor.Dispose();
        _ghostModeController.Dispose();
        _limitsCoordinator.SnapshotChanged -= LimitsCoordinator_SnapshotChanged;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        _limitsCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    private async void SystemEvents_PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || !_isLoaded)
        {
            return;
        }

        try
        {
            // Let network, named pipes and desktop CLIs settle after resume.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1_000, 5_001)));
            if (_isLoaded)
            {
                WidgetLogger.Info("Limits", "refresh_requested_after_resume");
                await _limitsCoordinator.RefreshAsync(force: true);
            }
        }
        catch (Exception exception)
        {
            WidgetLogger.Warning("Limits", "resume_refresh_failed", exception);
        }
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
