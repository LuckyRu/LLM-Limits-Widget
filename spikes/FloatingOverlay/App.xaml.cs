using System.Windows;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using DrawingSystemIcons = System.Drawing.SystemIcons;

namespace LLMLimitsWidget.FloatingOverlay;

public partial class App : System.Windows.Application
{
    private FormsNotifyIcon? _trayIcon;
    private FormsContextMenuStrip? _trayMenu;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
        MainWindow.Activate();

        _trayMenu = new FormsContextMenuStrip();
        _trayMenu.Items.Add(new FormsToolStripMenuItem("Показать виджет", null, (_, _) => ShowWidget()));
        _trayMenu.Items.Add(new FormsToolStripMenuItem("Выйти", null, (_, _) => Shutdown()));
        _trayIcon = new FormsNotifyIcon
        {
            Icon = DrawingSystemIcons.Application,
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

        _trayMenu?.Dispose();
        base.OnExit(e);
    }

    private void ShowWidget()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }
}
