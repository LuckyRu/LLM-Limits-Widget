using System.Drawing;
using System.IO;
using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
        MainWindow.Activate();

        _trayMenu = new FormsContextMenuStrip();
        _trayMenu.Items.Add(new FormsToolStripMenuItem("Показать виджет", null, (_, _) => ShowWidget()));
        _trayMenu.Items.Add(new FormsToolStripMenuItem("Сбросить позицию", null, (_, _) => ResetWidgetPosition()));
        _trayMenu.Items.Add(new FormsToolStripMenuItem("Выйти", null, (_, _) => Shutdown()));
        _trayIconAsset = LoadTrayIcon();
        _trayIcon = new FormsNotifyIcon
        {
            Icon = _trayIconAsset ?? SystemIcons.Application,
            Text = "LLM Limits Widget",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWidget();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _trayIconAsset?.Dispose();
        _trayIconStream?.Dispose();
        _trayMenu?.Dispose();
        base.OnExit(e);
    }

    private Icon? LoadTrayIcon()
    {
        _trayIconStream = typeof(App).Assembly.GetManifestResourceStream(
            "LLMLimitsWidget.FloatingOverlay.Assets.llm-limits-tray.ico");
        return _trayIconStream is null ? null : new Icon(_trayIconStream);
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
        MainWindow.Activate();
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
        widget.Activate();
    }
}
