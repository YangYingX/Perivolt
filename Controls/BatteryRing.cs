using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace PowerHub.Controls;

public sealed class BatteryRing : Control
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(int?), typeof(BatteryRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(BatteryRing),
        new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double thickness && double.IsFinite(thickness) && thickness > 0);

    static BatteryRing()
    {
        ForegroundProperty.OverrideMetadata(typeof(BatteryRing),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(BatteryRing), new FrameworkPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(BatteryRing), new FrameworkPropertyMetadata(false));
    }

    public int? Percent
    {
        get => (int?)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var radius = (Math.Min(ActualWidth, ActualHeight) - StrokeThickness) / 2;
        if (radius <= 0) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(null, new Pen(TrackBrush, StrokeThickness), center, radius, radius);
        if (Percent is not int value || value <= 0) return;

        var pen = new Pen(Foreground, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 100)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = Math.Clamp(value, 0, 100) * Math.PI / 50 - Math.PI / 2;
        var end = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            context.ArcTo(end, new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }
}
