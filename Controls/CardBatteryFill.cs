using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace PowerHub.Controls;

public sealed class CardBatteryFill : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(int?), typeof(CardBatteryFill), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionChanged));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(CardBatteryFill), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender,
        (sender, _) => ((CardBatteryFill)sender)._horizontalFill = null));
    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(nameof(FillOpacity), typeof(double), typeof(CardBatteryFill), new FrameworkPropertyMetadata(.85, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorOpacityProperty = DependencyProperty.Register(nameof(ColorOpacity), typeof(double), typeof(CardBatteryFill), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionChanged));
    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.Register(nameof(AnimationsEnabled), typeof(bool), typeof(CardBatteryFill), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionChanged));
    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(nameof(IsCharging), typeof(bool), typeof(CardBatteryFill), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionChanged));
    public static readonly DependencyProperty AnimationIntensityProperty = DependencyProperty.Register(nameof(AnimationIntensity), typeof(double), typeof(CardBatteryFill), new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UseSubtleFillProperty = DependencyProperty.Register(nameof(UseSubtleFill), typeof(bool), typeof(CardBatteryFill), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Brush AmbientMask = CreateMask();
    private static readonly Brush Sheen = CreateSheen();
    [ThreadStatic] private static Dictionary<double, Brush>? _endMasks;
    private Brush? _horizontalFill;
    private bool _subscribed;
    private double _lastFrame;
    private double _phase;

    public CardBatteryFill()
    {
        Loaded += (_, _) => UpdateRendering();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateRendering();
    }

    public int? Percent { get => (int?)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double FillOpacity { get => (double)GetValue(FillOpacityProperty); set => SetValue(FillOpacityProperty, value); }
    public double ColorOpacity { get => (double)GetValue(ColorOpacityProperty); set => SetValue(ColorOpacityProperty, value); }
    public bool AnimationsEnabled { get => (bool)GetValue(AnimationsEnabledProperty); set => SetValue(AnimationsEnabledProperty, value); }
    public bool IsCharging { get => (bool)GetValue(IsChargingProperty); set => SetValue(IsChargingProperty, value); }
    public double AnimationIntensity { get => (double)GetValue(AnimationIntensityProperty); set => SetValue(AnimationIntensityProperty, value); }
    public bool UseSubtleFill { get => (bool)GetValue(UseSubtleFillProperty); set => SetValue(UseSubtleFillProperty, value); }

    private static void OnMotionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((CardBatteryFill)sender).UpdateRendering();

    private void UpdateRendering()
    {
        if (IsLoaded && IsVisible && AnimationsEnabled && IsCharging && Percent is >= 0 and <= 100 && ColorOpacity > 0)
        {
            if (_subscribed) return;
            _lastFrame = 0;
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
        else StopRendering();
    }

    private void StopRendering()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now - _lastFrame < 1d / 24) return;
        _lastFrame = _phase = now;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || Percent is null || ColorOpacity <= 0) return;
        context.PushOpacity(Math.Clamp(FillOpacity * ColorOpacity, 0, 1));
        DrawSurface(context, new Rect(RenderSize), _horizontalFill ??= CreateHorizontalBrush(Fill),
            Math.Clamp(Percent.Value, 0, 100) / 100d, 1, IsCharging && AnimationsEnabled, _phase, AnimationIntensity, UseSubtleFill);
        context.Pop();
    }

    internal static void DrawSurface(DrawingContext context, Rect bounds, Brush fill, double percent,
        double opacity, bool isCharging, double phase, double intensity, bool subtle = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        context.PushOpacity(opacity);
        context.PushClip(new RectangleGeometry(bounds, 12, 12));
        Rect rail;
        if (!subtle || bounds.Height <= 6) rail = bounds;
        else
        {
            var breathing = isCharging ? (Math.Sin(phase * 1.5) + 1) / 2 : 0;
            context.PushOpacity(.14 + .035 * intensity * breathing);
            context.PushOpacityMask(AmbientMask);
            context.DrawRectangle(fill, null, bounds);
            context.Pop(); context.Pop();
            var inset = Math.Min(18, bounds.Width * .12);
            rail = new Rect(bounds.Left + inset, bounds.Bottom - Math.Min(7, bounds.Height * .14),
                Math.Max(0, bounds.Width - 2 * inset), 3);
            context.PushOpacity(.12);
            context.DrawRoundedRectangle(fill, null, rail, 1.5, 1.5);
            context.Pop();
        }
        var charged = new Rect(rail.X, rail.Y, rail.Width * Math.Clamp(percent, 0, 1), rail.Height);
        if (charged.Width > 0)
        {
            var fadeEnd = !subtle && percent < 1;
            if (fadeEnd) context.PushOpacityMask(EndMask(charged.Width));
            if (subtle) context.DrawRoundedRectangle(fill, null, charged, 1.5, 1.5);
            else context.DrawRectangle(fill, null, charged);
            if (isCharging)
            {
                context.PushClip(new RectangleGeometry(charged, subtle ? 1.5 : 0, subtle ? 1.5 : 0));
                var width = Math.Max(12, rail.Width * .22);
                var x = rail.Left - width + (phase * .35 % 1) * (charged.Width + width);
                context.PushOpacity(Math.Min(.8, (subtle ? .3 : .12) * intensity));
                context.DrawRectangle(Sheen, null, new Rect(x, rail.Y, width, rail.Height));
                context.Pop(); context.Pop();
            }
            if (fadeEnd) context.Pop();
        }
        context.Pop(); context.Pop();
    }

    private static Brush CreateMask()
    {
        var mask = new LinearGradientBrush(Colors.Transparent, Colors.White, new Point(0, 0), new Point(0, 1));
        mask.Freeze();
        return mask;
    }

    private static Brush EndMask(double width)
    {
        var masks = _endMasks ??= new Dictionary<double, Brush>();
        if (masks.TryGetValue(width, out var mask)) return mask;
        var fade = Math.Min(12, width / 2);
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.White, 0), new(Colors.White, 1 - fade / width), new(Colors.Transparent, 1)
        }, new Point(0, 0), new Point(1, 0));
        brush.Freeze();
        if (masks.Count >= 64) masks.Clear();
        return masks[width] = brush;
    }

    private static Brush CreateSheen()
    {
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.Transparent, 0), new(Colors.White, .5), new(Colors.Transparent, 1)
        }, new Point(0, 0), new Point(1, 0));
        brush.Freeze();
        return brush;
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
