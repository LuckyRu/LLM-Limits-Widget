namespace LLMLimitsWidget.FloatingOverlay;

public sealed class WidgetAppearance
{
    public double VerticalWidth { get; init; } = 285;
    public double VerticalHeight { get; init; } = 103;
    public double HorizontalWidth { get; set; } = 545;
    public double HorizontalHeight { get; init; } = 55;
    public double Scale { get; set; } = 1.0;
    public double SurfaceOpacity { get; set; } = 1.0;
    public double CornerRadius { get; set; } = 18;
    public LayoutOrientation Orientation { get; set; } = LayoutOrientation.Vertical;

    public double BaseWidth => Orientation == LayoutOrientation.Vertical ? VerticalWidth : HorizontalWidth;
    public double BaseHeight => Orientation == LayoutOrientation.Vertical ? VerticalHeight : HorizontalHeight;
}
