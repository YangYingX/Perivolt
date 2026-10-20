using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Control = System.Windows.Controls.Control;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace PowerHub.Controls;

public sealed class OutlinedText : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsOutlinedProperty = DependencyProperty.Register(nameof(IsOutlined), typeof(bool), typeof(OutlinedText), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(nameof(TextTrimming), typeof(TextTrimming), typeof(OutlinedText), new FrameworkPropertyMetadata(TextTrimming.CharacterEllipsis, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsOutlined { get => (bool)GetValue(IsOutlinedProperty); set => SetValue(IsOutlinedProperty, value); }
    public TextTrimming TextTrimming { get => (TextTrimming)GetValue(TextTrimmingProperty); set => SetValue(TextTrimmingProperty, value); }
    private FormattedText Format(double width)
    {
        var text = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        text.MaxTextWidth = Math.Max(1, Math.Min(100000, double.IsFinite(width) ? width : 100000));
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming;
        return text;
    }
    protected override Size MeasureOverride(Size constraint)
    {
        var text = Format(constraint.Width);
        return new Size(Math.Min(text.WidthIncludingTrailingWhitespace + 2, constraint.Width), text.Height + 2);
    }
    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var text = Format(ActualWidth);
        var origin = new Point(1, Math.Max(0, (ActualHeight - text.Height) / 2));
        if (IsOutlined)
        {
            var color = (Foreground as SolidColorBrush)?.Color ?? Colors.Black;
            var outline = new SolidColorBrush(Color.FromRgb((byte)(255 - color.R), (byte)(255 - color.G), (byte)(255 - color.B)));
            outline.Freeze();
            var geometry = text.BuildGeometry(origin);
            context.DrawGeometry(Foreground, new Pen(outline, 1.4) { LineJoin = PenLineJoin.Round }, geometry);
            context.DrawGeometry(Foreground, null, geometry);
        }
        else context.DrawText(text, origin);
    }
}
