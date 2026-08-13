using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;

namespace LLMLimitsWidget.FloatingOverlay;

public partial class LimitMetricControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LimitMetricControl),
        new FrameworkPropertyMetadata(0d, OnMetricChanged));

    public static readonly DependencyProperty PercentTextProperty = DependencyProperty.Register(
        nameof(PercentText), typeof(string), typeof(LimitMetricControl),
        new FrameworkPropertyMetadata(string.Empty, OnMetricChanged));

    public static readonly DependencyProperty PeriodProperty = DependencyProperty.Register(
        nameof(Period), typeof(string), typeof(LimitMetricControl),
        new FrameworkPropertyMetadata(string.Empty, OnMetricChanged));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(WpfBrush), typeof(LimitMetricControl),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.White, OnMetricChanged));

    public LimitMetricControl()
    {
        InitializeComponent();
        UpdateMetric();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string PercentText
    {
        get => (string)GetValue(PercentTextProperty);
        set => SetValue(PercentTextProperty, value);
    }

    public string Period
    {
        get => (string)GetValue(PeriodProperty);
        set => SetValue(PeriodProperty, value);
    }

    public WpfBrush Accent
    {
        get => (WpfBrush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static void OnMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LimitMetricControl)d).UpdateMetric();
    }

    private void UpdateMetric()
    {
        if (Meter is null || PeriodLabel is null)
        {
            return;
        }

        Meter.Value = Value;
        Meter.Label = PercentText;
        Meter.Accent = Accent;
        PeriodLabel.Text = Period;
        PeriodLabel.Foreground = Accent;
    }
}
