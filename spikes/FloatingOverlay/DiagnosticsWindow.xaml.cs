using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using LLMLimitsWidget.Presentation;

namespace LLMLimitsWidget.FloatingOverlay;

public partial class DiagnosticsWindow : Window
{
    private readonly ArchitectureV2CompositionRoot _composition;
    private readonly DiagnosticsViewModel _viewModel = new();
    private readonly DispatcherTimer _timer;

    public DiagnosticsWindow(ArchitectureV2CompositionRoot composition)
    {
        _composition = composition;
        InitializeComponent();
        DataContext = _viewModel;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => ApplyState();
        Loaded += (_, _) =>
        {
            ApplyState();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _composition.RequestManualRefreshAsync();
            ApplyState();
        }
        catch (Exception exception)
        {
            WidgetLogger.Warning("Diagnostics", "manual_refresh_failed", exception);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WidgetLogger.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            WidgetLogger.Warning("Diagnostics", "open_logs_failed", exception);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyState() => _viewModel.Apply(_composition.CurrentState, DateTimeOffset.UtcNow);
}
