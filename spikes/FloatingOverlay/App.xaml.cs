using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace LLMLimitsWidget.FloatingOverlay;

public partial class App : System.Windows.Application
{
    private FormsNotifyIcon? _trayIcon;
    private FormsContextMenuStrip? _trayMenu;
    private Icon? _trayIconAsset;
    private Stream? _trayIconStream;
    private FormsToolStripMenuItem? _ghostModeMenuItem;
    private Mutex? _singleInstanceMutex;
    private bool _singleInstanceMutexOwned;
    private IntPtr _foregroundBeforeTray;
    private bool _widgetHiddenForTrayRecovery;
    private bool _appExiting;
    private ArchitectureV2CompositionRoot? _architectureV2;
    private Task? _architectureV2StartTask;

    public App()
    {
        WidgetLogger.Initialize();
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        WidgetLogger.Info(
            "App",
            "startup_begin",
            ("arguments", string.Join(" ", e.Args)));
        base.OnStartup(e);

        var architectureV2Enabled = ArchitectureV2CompositionRoot.IsEnabled(e.Args);

        if (!TryAcquireSingleInstance(architectureV2Enabled))
        {
            WidgetLogger.Info("App", "duplicate_instance_exit");
            Shutdown();
            return;
        }

        var suppressPersistedGhost = e.Args.Any(
            argument => string.Equals(argument, "--no-ghost", StringComparison.OrdinalIgnoreCase));
        MainWindow = new MainWindow(suppressPersistedGhost, architectureV2Enabled);

        if (architectureV2Enabled)
        {
            _architectureV2 = ArchitectureV2CompositionRoot.Create(Dispatcher);
            ((MainWindow)MainWindow).AttachArchitectureV2(_architectureV2);
            _architectureV2StartTask = StartArchitectureV2Async(_architectureV2);
            WidgetLogger.Info("ArchitectureV2", "composition_feature_enabled");
        }

        try
        {
            _trayMenu = new FormsContextMenuStrip();
            _ghostModeMenuItem = new FormsToolStripMenuItem("Режим призрака")
            {
                CheckOnClick = false
            };
            _ghostModeMenuItem.Click += (_, _) =>
            {
                if (MainWindow is MainWindow widget)
                {
                    var result = widget.SetGhostMode(
                        !(widget.IsGhostModeEnabled || widget.GhostCleanupRequired),
                        _foregroundBeforeTray);
                    UpdateGhostMenuStatus(widget, result);
                    EnsureTrayMenuAboveOverlay();
                }
            };
            _trayMenu.Opening += (_, _) =>
            {
                CaptureForegroundBeforeTray();
                var overlayDemoted = true;
                if (MainWindow is MainWindow widget)
                {
                    overlayDemoted = widget.SetTrayMenuOpen(true);
                }
                var menuRaised = EnsureTrayMenuAboveOverlay();
                if (ManagementMenuZOrder.ShouldHideOverlayForRecovery(overlayDemoted, menuRaised)
                    && MainWindow is MainWindow { IsVisible: true } failedWidget)
                {
                    failedWidget.ReportManagementMenuFailure();
                    failedWidget.Hide();
                    _widgetHiddenForTrayRecovery = true;
                }
            };
            _trayMenu.Opened += (_, _) =>
            {
                if (MainWindow is MainWindow widget)
                {
                    UpdateGhostMenuStatus(widget, widget.LastGhostModeResult);
                }
            };
            _trayMenu.Closed += (_, _) =>
            {
                if (MainWindow is MainWindow widget)
                {
                    widget.SetTrayMenuOpen(false);
                    if (_widgetHiddenForTrayRecovery && !_appExiting)
                    {
                        widget.Show();
                        widget.EnsureVisible();
                    }
                }
                _widgetHiddenForTrayRecovery = false;
                _foregroundBeforeTray = IntPtr.Zero;
            };
            _trayMenu.Items.Add(_ghostModeMenuItem);
            _trayMenu.Items.Add(new FormsToolStripMenuItem("Показать виджет", null, (_, _) => ShowWidget()));
            _trayMenu.Items.Add(new FormsToolStripMenuItem("Сбросить позицию", null, (_, _) => ResetWidgetPosition()));
            _trayMenu.Items.Add(new FormsToolStripMenuItem("Выйти", null, (_, _) => ExitApplication()));
            _trayIconAsset = LoadTrayIcon();
            _trayIcon = new FormsNotifyIcon
            {
                Icon = _trayIconAsset ?? SystemIcons.Application,
                Text = "LLM Limits Widget",
                Visible = true,
                ContextMenuStrip = _trayMenu
            };
            _trayIcon.MouseDown += (_, _) =>
            {
                _foregroundBeforeTray = IntPtr.Zero;
                CaptureForegroundBeforeTray();
            };
            _trayIcon.DoubleClick += (_, _) => ShowWidget();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ExternalException
                                          or ArgumentException)
        {
            WidgetLogger.Error("Tray", "tray_initialization_failed", exception);
            _trayIcon?.Dispose();
            _trayIcon = null;
            _trayMenu?.Dispose();
            _trayMenu = null;
        }

        if (MainWindow is MainWindow initialWidget)
        {
            initialWidget.SetRecoveryChannelAvailable(_trayIcon is not null);
            initialWidget.GhostModeChanged += (_, enabled) =>
            {
                UpdateGhostMenuStatus(initialWidget, initialWidget.LastGhostModeResult);
            };
        }

        MainWindow.Show();
        if (MainWindow is MainWindow loadedWidget)
        {
            if (suppressPersistedGhost)
            {
                loadedWidget.ResetWidgetPosition();
            }
            UpdateGhostMenuStatus(loadedWidget, loadedWidget.LastGhostModeResult);
        }
        if (MainWindow is MainWindow shownWidget && !shownWidget.IsGhostInputSuppressed)
        {
            MainWindow.Activate();
        }

        WidgetLogger.Info(
            "App",
            "startup_complete",
            ("trayAvailable", _trayIcon is not null),
            ("ghostMode", MainWindow is MainWindow widget && widget.IsGhostModeEnabled));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WidgetLogger.Info("App", "shutdown_begin", ("exitCode", e.ApplicationExitCode));
        _appExiting = true;
        StopArchitectureV2();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _trayIconAsset?.Dispose();
        _trayIconStream?.Dispose();
        _trayMenu?.Dispose();
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private async Task StartArchitectureV2Async(ArchitectureV2CompositionRoot composition)
    {
        try
        {
            await composition.StartAsync().ConfigureAwait(false);
            WidgetLogger.Info("ArchitectureV2", "composition_started");
        }
        catch (Exception exception)
        {
            WidgetLogger.Error("ArchitectureV2", "composition_start_failed", exception);
            await composition.DisposeAsync().ConfigureAwait(false);
            _architectureV2 = null;
        }
    }

    private void StopArchitectureV2()
    {
        if (_architectureV2 is null)
        {
            return;
        }

        try
        {
            _architectureV2StartTask?.GetAwaiter().GetResult();
            _architectureV2.DisposeAsync().AsTask().GetAwaiter().GetResult();
            WidgetLogger.Info("ArchitectureV2", "composition_stopped");
        }
        catch (Exception exception)
        {
            WidgetLogger.Error("ArchitectureV2", "composition_stop_failed", exception);
        }
        finally
        {
            _architectureV2 = null;
            _architectureV2StartTask = null;
        }
    }

    private bool TryAcquireSingleInstance(bool architectureV2Enabled)
    {
        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: false,
                name: architectureV2Enabled
                    ? "Local\\LLMLimitsWidget.FloatingOverlay.ArchitectureV2"
                    : "Local\\LLMLimitsWidget.FloatingOverlay",
                createdNew: out _);
            try
            {
                if (_singleInstanceMutex.WaitOne(0))
                {
                    _singleInstanceMutexOwned = true;
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                _singleInstanceMutexOwned = true;
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }
        catch (Exception exception)
        {
            WidgetLogger.Critical("App", "single_instance_check_failed", exception);
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            return false;
        }
    }

    private void ReleaseSingleInstance()
    {
        if (_singleInstanceMutex is null)
        {
            return;
        }

        try
        {
            if (_singleInstanceMutexOwned)
            {
                _singleInstanceMutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
            // The mutex may already have been released by the operating system.
        }
        finally
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            _singleInstanceMutexOwned = false;
        }
    }

    private void App_DispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        WidgetLogger.Critical("Wpf", "dispatcher_unhandled_exception", e.Exception);
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WidgetLogger.Critical("Runtime", "unhandled_exception", exception, ("terminating", e.IsTerminating));
        }
        else
        {
            WidgetLogger.Critical("Runtime", "unhandled_non_exception", null, ("terminating", e.IsTerminating));
        }
    }

    private void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        WidgetLogger.Error("Runtime", "unobserved_task_exception", e.Exception);
        e.SetObserved();
    }

    private void ExitApplication()
    {
        _appExiting = true;
        Shutdown();
    }

    private Icon? LoadTrayIcon()
    {
        _trayIconStream = typeof(App).Assembly.GetManifestResourceStream(
            "LLMLimitsWidget.FloatingOverlay.Assets.llm-limits-tray.ico");
        return _trayIconStream is null ? null : new Icon(_trayIconStream);
    }

    private void CaptureForegroundBeforeTray()
    {
        var foreground = GetForegroundWindow();
        if (MainWindow is not MainWindow widget)
        {
            _foregroundBeforeTray = foreground;
            return;
        }

        var widgetHandle = new WindowInteropHelper(widget).Handle;
        if (IsEligibleExternalWindow(_foregroundBeforeTray, widgetHandle))
        {
            return;
        }

        if (IsEligibleExternalWindow(foreground, widgetHandle))
        {
            _foregroundBeforeTray = foreground;
            return;
        }

        _foregroundBeforeTray = FindNextExternalWindow(widgetHandle);
    }

    private static IntPtr FindNextExternalWindow(IntPtr widgetHandle)
    {
        for (var candidate = GetWindow(widgetHandle, 2);
             candidate != IntPtr.Zero;
             candidate = GetWindow(candidate, 2))
        {
            if (IsEligibleExternalWindow(candidate, widgetHandle))
            {
                return candidate;
            }
        }

        return IntPtr.Zero;
    }

    private static bool IsEligibleExternalWindow(IntPtr candidate, IntPtr widgetHandle)
    {
        if (candidate == IntPtr.Zero
            || candidate == widgetHandle
            || !IsWindowVisible(candidate)
            || GetAncestor(candidate, 2) != candidate)
        {
            return false;
        }

        GetWindowThreadProcessId(candidate, out var processId);
        return processId != (uint)Environment.ProcessId;
    }

    private void UpdateGhostMenuStatus(MainWindow widget, GhostModeTransitionResult result)
    {
        if (_ghostModeMenuItem is null)
        {
            return;
        }

        _ghostModeMenuItem.Checked = widget.IsGhostModeEnabled || widget.GhostCleanupRequired;
        var failed = result is not (GhostModeTransitionResult.Success
            or GhostModeTransitionResult.AlreadyInRequestedState)
            || widget.GhostCleanupRequired;
        _ghostModeMenuItem.Text = widget.GhostCleanupRequired
            ? "Режим призрака (требуется восстановление)"
            : failed
                ? "Режим призрака (ошибка)"
                : "Режим призрака";
        _ghostModeMenuItem.ToolTipText = failed
            ? $"Не удалось изменить режим: {result}"
            : widget.HooksAvailable
                ? "Виджет виден, но пропускает весь ввод"
                : "Ввод пропускается; topmost поддерживается резервным watchdog";
    }

    private bool EnsureTrayMenuAboveOverlay()
    {
        if (_trayMenu is not null && !_trayMenu.IsDisposed)
        {
            return ManagementMenuZOrder.EnsureAboveOverlay(_trayMenu.Handle);
        }

        return false;
    }

    private void ShowWidget()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        if (MainWindow is MainWindow widget)
        {
            widget.EnsureVisible();
        }
        if (MainWindow is MainWindow shownWidget && !shownWidget.IsGhostInputSuppressed)
        {
            MainWindow.Activate();
        }
    }

    private void ResetWidgetPosition()
    {
        if (MainWindow is not MainWindow widget)
        {
            return;
        }

        widget.Show();
        widget.WindowState = WindowState.Normal;
        widget.ResetWidgetPosition();
        if (!widget.IsGhostInputSuppressed)
        {
            widget.Activate();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
