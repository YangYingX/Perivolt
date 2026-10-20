using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace PowerHub.Controls;

public sealed class CardBatteryFill : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(int?), typeof(CardBatteryFill), new FrameworkPropertyMetadata(null, OnPercentChanged));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(CardBatteryFill), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(nameof(FillOpacity), typeof(double), typeof(CardBatteryFill), new FrameworkPropertyMetadata(.85, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorOpacityProperty = DependencyProperty.Register(nameof(ColorOpacity), typeof(double), typeof(CardBatteryFill), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.Register(nameof(AnimationsEnabled), typeof(bool), typeof(CardBatteryFill), new FrameworkPropertyMetadata(true));
    private static readonly DependencyProperty DisplayedProperty = DependencyProperty.Register("Displayed", typeof(double), typeof(CardBatteryFill), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public int? Percent { get => (int?)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double FillOpacity { get => (double)GetValue(FillOpacityProperty); set => SetValue(FillOpacityProperty, value); }
    public double ColorOpacity { get => (double)GetValue(ColorOpacityProperty); set => SetValue(ColorOpacityProperty, value); }
    public bool AnimationsEnabled { get => (bool)GetValue(AnimationsEnabledProperty); set => SetValue(AnimationsEnabledProperty, value); }
    private static void OnPercentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var fill = (CardBatteryFill)sender;
        var current = (double)fill.GetValue(DisplayedProperty);
        var target = fill.Percent is int percent ? Math.Clamp(percent, 0, 100) / 100d : 0;
        fill.BeginAnimation(DisplayedProperty, null);
        fill.SetValue(DisplayedProperty, target);
        if (fill.IsVisible && fill.AnimationsEnabled && SystemParameters.ClientAreaAnimation)
            fill.BeginAnimation(DisplayedProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(240))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || Percent is null) return;
        var bounds = new Rect(RenderSize);
        context.PushClip(new RectangleGeometry(bounds, 12, 12));
        context.PushOpacity(Math.Clamp(FillOpacity * ColorOpacity, 0, 1));
        context.DrawRectangle(Fill, null, new Rect(0, 0, ActualWidth * Math.Clamp((double)GetValue(DisplayedProperty), 0, 1), ActualHeight));
        context.Pop(); context.Pop();
    }
}
