using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;

namespace LLMLimitsWidget.FloatingOverlay;

public sealed class CircularMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CircularMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(CircularMeter),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(WpfBrush), typeof(CircularMeter),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public WpfBrush Accent
    {
        get => (WpfBrush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var center = new WpfPoint(ActualWidth / 2, ActualHeight / 2);
        var radius = (size / 2) - 4;
        var trackPen = new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 53, 62)), 4);
        trackPen.Freeze();
        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        var value = Math.Clamp(Value, 0, 100);
        if (value > 0)
        {
            var accentPen = new System.Windows.Media.Pen(Accent, 4)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };

            if (value >= 99.9)
            {
                drawingContext.DrawEllipse(null, accentPen, center, radius, radius);
            }
            else
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    var start = PointOnCircle(center, radius, -90);
                    var end = PointOnCircle(center, radius, -90 + (360 * value / 100));
                    context.BeginFigure(start, false, false);
                    context.ArcTo(end, new System.Windows.Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
                }

                geometry.Freeze();
                drawingContext.DrawGeometry(null, accentPen, geometry);
            }
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            Label,
            CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            Label.Length > 5 ? 10 : 12,
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 247)),
            dpi);
        drawingContext.DrawText(text, new WpfPoint(center.X - (text.Width / 2), center.Y - (text.Height / 2) + 1));
    }

    private static WpfPoint PointOnCircle(WpfPoint center, double radius, double angleDegrees)
    {
        var angle = angleDegrees * Math.PI / 180;
        return new WpfPoint(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));
    }
}
