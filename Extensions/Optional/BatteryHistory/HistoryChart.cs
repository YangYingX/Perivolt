using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Perivolt.Extensions.BatteryHistory;

internal sealed class HistoryChart : FrameworkElement
{
    private static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    private Brush Accent => (Brush)GetValue(AccentProperty);
    private Brush Text => (Brush)GetValue(TextProperty);
    private Brush GridBrush => (Brush)GetValue(GridBrushProperty);
    private IReadOnlyList<HistorySample> _samples = [];
    private DateTimeOffset _start, _end;
    private string _empty = "";

    internal HistoryChart()
    {
        SetResourceReference(AccentProperty, "Accent");
        SetResourceReference(TextProperty, "TextSecondary");
        SetResourceReference(GridBrushProperty, "Stroke");
    }

    internal void SetData(IReadOnlyList<HistorySample> samples, DateTimeOffset start, DateTimeOffset end, string empty)
    {
        _samples = samples; _start = start; _end = end; _empty = empty;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth < 100 || ActualHeight < 70 || _end <= _start) return;
        var plot = new Rect(38, 12, ActualWidth - 50, ActualHeight - 42);
        var gridPen = new Pen(GridBrush, .8);
        for (int percent = 0; percent <= 100; percent += 25)
        {
            var y = plot.Bottom - plot.Height * percent / 100;
            context.PushOpacity(.55);
            context.DrawLine(gridPen, new(plot.Left, y), new(plot.Right, y));
            context.Pop();
            var label = Format(percent + "%");
            context.DrawText(label, new(plot.Left - label.Width - 8, y - label.Height / 2));
        }
        var span = _end - _start;
        for (int index = 0; index < 3; index++)
        {
            var at = _start + TimeSpan.FromTicks(span.Ticks * index / 2);
            var label = Format(at.ToLocalTime().ToString(span.TotalDays > 2 ? "MM-dd HH:mm" : "HH:mm"));
            var x = plot.Left + plot.Width * index / 2;
            context.DrawText(label, new(Math.Clamp(x - label.Width / 2, 0, ActualWidth - label.Width), plot.Bottom + 10));
        }
        if (_samples.Count == 0)
        {
            var empty = Format(_empty, 12);
            context.DrawText(empty, new(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + (plot.Height - empty.Height) / 2));
            return;
        }
        Point Position(HistorySample sample) => new(plot.Left + (sample.At - _start).Ticks / (double)span.Ticks * plot.Width,
            plot.Bottom - sample.Percent / 100d * plot.Height);
        var line = new StreamGeometry();
        using (var path = line.Open())
        {
            HistorySample? previous = null;
            foreach (var sample in _samples)
            {
                var point = Position(sample);
                if (previous is null || sample.BreakBefore || sample.At - previous.At > HistoryStore.MaxLineGap)
                    path.BeginFigure(point, false, false);
                else path.LineTo(point, true, false);
                previous = sample;
            }
        }
        line.Freeze();
        var pen = new Pen(Accent, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        context.PushClip(new RectangleGeometry(new Rect(plot.Left - 3, plot.Top - 3, plot.Width + 6, plot.Height + 6)));
        context.DrawGeometry(null, pen, line);
        for (int index = 0; index < _samples.Count; index++)
            if (_samples.Count <= 60 || _samples[index].BreakBefore || index == 0 || index == _samples.Count - 1)
                context.DrawEllipse(Accent, null, Position(_samples[index]), 2.5, 2.5);
        context.Pop();
    }

    private FormattedText Format(string text, double size = 10) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI, Microsoft YaHei UI"), size, Text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
