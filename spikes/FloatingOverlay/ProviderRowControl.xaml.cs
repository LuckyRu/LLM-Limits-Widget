using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;

namespace LLMLimitsWidget.FloatingOverlay;

public partial class ProviderRowControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(ProviderLogoKind), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(ProviderLogoKind.OpenAi, OnRowChanged));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(WpfBrush), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.White, OnRowChanged));

    public static readonly DependencyProperty MetricOneValueProperty = DependencyProperty.Register(
        nameof(MetricOneValue), typeof(double), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(0d, OnRowChanged));

    public static readonly DependencyProperty MetricOnePercentProperty = DependencyProperty.Register(
        nameof(MetricOnePercent), typeof(string), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(string.Empty, OnRowChanged));

    public static readonly DependencyProperty MetricOnePeriodProperty = DependencyProperty.Register(
        nameof(MetricOnePeriod), typeof(string), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(string.Empty, OnRowChanged));

    public static readonly DependencyProperty MetricTwoValueProperty = DependencyProperty.Register(
        nameof(MetricTwoValue), typeof(double), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(0d, OnRowChanged));

    public static readonly DependencyProperty MetricTwoPercentProperty = DependencyProperty.Register(
        nameof(MetricTwoPercent), typeof(string), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(string.Empty, OnRowChanged));

    public static readonly DependencyProperty MetricTwoPeriodProperty = DependencyProperty.Register(
        nameof(MetricTwoPeriod), typeof(string), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(string.Empty, OnRowChanged));

    public static readonly DependencyProperty HasSecondMetricProperty = DependencyProperty.Register(
        nameof(HasSecondMetric), typeof(bool), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(false, OnRowChanged));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(false, OnRowChanged));

    public static readonly DependencyProperty CountdownProperty = DependencyProperty.Register(
        nameof(Countdown), typeof(string), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(string.Empty, OnRowChanged));

    public static readonly DependencyProperty ResetLabelProperty = DependencyProperty.Register(
        nameof(ResetLabel), typeof(string), typeof(ProviderRowControl),
        new FrameworkPropertyMetadata(string.Empty, OnRowChanged));

    public ProviderRowControl()
    {
        InitializeComponent();
        UpdateRow();
    }

    public ProviderLogoKind Provider { get => (ProviderLogoKind)GetValue(ProviderProperty); set => SetValue(ProviderProperty, value); }
    public WpfBrush Accent { get => (WpfBrush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double MetricOneValue { get => (double)GetValue(MetricOneValueProperty); set => SetValue(MetricOneValueProperty, value); }
    public string MetricOnePercent { get => (string)GetValue(MetricOnePercentProperty); set => SetValue(MetricOnePercentProperty, value); }
    public string MetricOnePeriod { get => (string)GetValue(MetricOnePeriodProperty); set => SetValue(MetricOnePeriodProperty, value); }
    public double MetricTwoValue { get => (double)GetValue(MetricTwoValueProperty); set => SetValue(MetricTwoValueProperty, value); }
    public string MetricTwoPercent { get => (string)GetValue(MetricTwoPercentProperty); set => SetValue(MetricTwoPercentProperty, value); }
    public string MetricTwoPeriod { get => (string)GetValue(MetricTwoPeriodProperty); set => SetValue(MetricTwoPeriodProperty, value); }
    public bool HasSecondMetric { get => (bool)GetValue(HasSecondMetricProperty); set => SetValue(HasSecondMetricProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public string Countdown { get => (string)GetValue(CountdownProperty); set => SetValue(CountdownProperty, value); }
    public string ResetLabel { get => (string)GetValue(ResetLabelProperty); set => SetValue(ResetLabelProperty, value); }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ProviderRowControl)d).UpdateRow();
    }

    private void UpdateRow()
    {
        if (ProviderMark is null || MetricOne is null || MetricTwo is null)
        {
            return;
        }

        ProviderSurface.Background = Accent;
        ProviderMark.Provider = Provider;
        ProviderMark.Foreground = Provider == ProviderLogoKind.Claude
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 23, 16))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 33, 38));

        MetricOne.Value = MetricOneValue;
        MetricOne.PercentText = FormatPercent(MetricOnePercent);
        MetricOne.Period = MetricOnePeriod;
        MetricOne.Accent = Accent;

        MetricTwo.Value = MetricTwoValue;
        MetricTwo.PercentText = FormatPercent(MetricTwoPercent);
        MetricTwo.Period = MetricTwoPeriod;
        MetricTwo.Accent = Accent;
        var metricWidth = Compact ? 62 : 75;
        ProviderColumn.Width = new GridLength(Compact ? 26 : 28);
        MetricOneColumn.Width = new GridLength(metricWidth);
        MetricTwo.Visibility = HasSecondMetric ? Visibility.Visible : Visibility.Collapsed;
        MetricTwoColumn.Width = HasSecondMetric ? new GridLength(metricWidth) : new GridLength(0);
        MetricOne.Width = metricWidth;
        MetricTwo.Width = metricWidth;

        CountdownText.Text = Countdown;
        ResetText.Text = ResetLabel;
    }

    private static string FormatPercent(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimEnd().EndsWith('%'))
        {
            return text;
        }

        var numberText = text.Trim().TrimEnd('%').Trim();
        if (!decimal.TryParse(numberText, NumberStyles.Number, CultureInfo.CurrentCulture, out var value))
        {
            return text;
        }

        return $"{value.ToString("0.##", CultureInfo.CurrentCulture)}%";
    }
}
