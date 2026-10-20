using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace PowerHub.Controls;

public sealed class CardBatteryFill : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(int?), typeof(CardBatteryFill), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(CardBatteryFill), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender,
        (sender, _) => ((CardBatteryFill)sender)._horizontalFill = null));
    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(nameof(FillOpacity), typeof(double), typeof(CardBatteryFill), new FrameworkPropertyMetadata(.85, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorOpacityProperty = DependencyProperty.Register(nameof(ColorOpacity), typeof(double), typeof(CardBatteryFill), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.Register(nameof(AnimationsEnabled), typeof(bool), typeof(CardBatteryFill), new FrameworkPropertyMetadata(true));
    private Brush? _horizontalFill;
    public int? Percent { get => (int?)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double FillOpacity { get => (double)GetValue(FillOpacityProperty); set => SetValue(FillOpacityProperty, value); }
    public double ColorOpacity { get => (double)GetValue(ColorOpacityProperty); set => SetValue(ColorOpacityProperty, value); }
    public bool AnimationsEnabled { get => (bool)GetValue(AnimationsEnabledProperty); set => SetValue(AnimationsEnabledProperty, value); }
    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || Percent is null) return;
        var bounds = new Rect(RenderSize);
        context.PushClip(new RectangleGeometry(bounds, 12, 12));
        context.PushOpacity(Math.Clamp(FillOpacity * ColorOpacity, 0, 1));
        context.DrawRectangle(_horizontalFill ??= CreateHorizontalBrush(Fill), null,
            new Rect(0, 0, ActualWidth * Math.Clamp(Percent.Value, 0, 100) / 100d, ActualHeight));
        context.Pop(); context.Pop();
    }

    internal static Brush CreateHorizontalBrush(Brush brush)
    {
        if (brush is not LinearGradientBrush gradient ||
            gradient.StartPoint == new System.Windows.Point(0, 0) && gradient.EndPoint == new System.Windows.Point(1, 0)) return brush;
        var horizontal = gradient.CloneCurrentValue();
        horizontal.StartPoint = new System.Windows.Point(0, 0);
        horizontal.EndPoint = new System.Windows.Point(1, 0);
        horizontal.Freeze();
        return horizontal;
    }
}
