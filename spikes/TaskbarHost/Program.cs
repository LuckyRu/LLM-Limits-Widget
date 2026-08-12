using System.Drawing;
using System.Windows.Forms;

namespace LLMLimitsWidget.TaskbarHost;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new WidgetApplicationContext());
    }
}

internal sealed class WidgetApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;

    public WidgetApplicationContext()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Показать статус", null, (_, _) => ShowStatus());
        _menu.Items.Add("Обновить", null, (_, _) => ShowStatus());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выйти", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "LLM Limits Widget",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatus();
    }

    private void ShowStatus()
    {
        using var window = new StatusForm();
        window.ShowDialog();
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class StatusForm : Form
{
    public StatusForm()
    {
        Text = "LLM Limits Widget";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 180);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            Location = new Point(18, 16),
            Text = "Лимиты подписок"
        };

        var chatGpt = new Label
        {
            AutoSize = true,
            Location = new Point(18, 58),
            Text = "ChatGPT Plus/Pro    63% · сброс 18 Aug"
        };

        var claude = new Label
        {
            AutoSize = true,
            Location = new Point(18, 92),
            Text = "Claude Pro              74% · сброс через 02:41"
        };

        var note = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Location = new Point(18, 136),
            Text = "Mock data · provider integration не подключена"
        };

        Controls.AddRange([title, chatGpt, claude, note]);
    }
}
