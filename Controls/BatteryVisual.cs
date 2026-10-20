using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace PowerHub.Controls;

public sealed class BatteryVisual : Control
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(int?), typeof(BatteryVisual),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPercentChanged));
    public static readonly DependencyProperty DisplayStyleProperty = DependencyProperty.Register(
        nameof(DisplayStyle), typeof(string), typeof(BatteryVisual),
        new FrameworkPropertyMetadata("Ring", FrameworkPropertyMetadataOptions.AffectsRender, OnMotionModeChanged));
    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.Register(
        nameof(AnimationsEnabled), typeof(bool), typeof(BatteryVisual),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnAnimationChanged));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(BatteryVisual),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));
    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(
        nameof(IsCharging), typeof(bool), typeof(BatteryVisual),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionModeChanged));
    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(
        nameof(FillOpacity), typeof(double), typeof(BatteryVisual),
        new FrameworkPropertyMetadata(.85, FrameworkPropertyMetadataOptions.AffectsRender, null,
            (_, value) => double.IsFinite((double)value) ? Math.Clamp((double)value, .1, 1) : .85));
    public static readonly DependencyProperty ColorOpacityProperty = DependencyProperty.Register(
        nameof(ColorOpacity), typeof(double), typeof(BatteryVisual),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionModeChanged,
            (_, value) => double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 1) : 1d));
    public static readonly DependencyProperty AnimationIntensityProperty = DependencyProperty.Register(
        nameof(AnimationIntensity), typeof(double), typeof(BatteryVisual),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender, null,
            (_, value) => double.IsFinite((double)value) ? Math.Clamp((double)value, .5, 3) : 1.6));

    private static readonly Point Center = new(35, 35);
    private static readonly Geometry OceanClip = Frozen(new EllipseGeometry(Center, 26.5, 26.5));
    private static readonly Geometry BarClip = Frozen(new RectangleGeometry(new Rect(9, 26, 47, 18), 3, 3));
    private static readonly Geometry BarSegments = CreateSegments();
    private static readonly Geometry ChargingBolt = Frozen(Geometry.Parse(
        "M59,5 L51,16 L56,16 L53,25 L63,12 L57.5,12 Z"));
    private static readonly Geometry SandVessel = Frozen(Geometry.Parse(
        "M24,13 L46,13 C46,23 43,28 37.5,33.8 C36.9,34.3 36.9,35.7 37.5,36.2 " +
        "C43,42 46,47 46,57 L24,57 C24,47 27,42 32.5,36.2 C33.1,35.7 33.1,34.3 32.5,33.8 C27,28 24,23 24,13 Z"));
    private static readonly Pen GlintPen = Frozen(new Pen(Brushes.White, .8));
    private static readonly Pen ChargingOutlinePen = Frozen(new Pen(Brushes.Black, 2) { LineJoin = PenLineJoin.Round });
    private Transform _layoutTransform = Transform.Identity;
    private Pen? _accentPen;
    private Pen? _trackPen;
    private Pen? _thinTrackPen;
    private Pen? _wavePen;
    private Brush? _horizontalFill;
    private bool _subscribed;
    private double _lastFrame;
    private double _phase = Now % (Math.PI * 20);
    private double _ringArcPercent = -1;
    private Geometry? _ringArc;

    static BatteryVisual()
    {
        ForegroundProperty.OverrideMetadata(typeof(BatteryVisual),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(BatteryVisual), new FrameworkPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(BatteryVisual), new FrameworkPropertyMetadata(false));
    }

    public BatteryVisual()
    {
        Loaded += (_, _) => UpdateRendering();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateRendering();
    }

    public int? Percent { get => (int?)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public string DisplayStyle { get => (string)GetValue(DisplayStyleProperty); set => SetValue(DisplayStyleProperty, value); }
    public bool AnimationsEnabled { get => (bool)GetValue(AnimationsEnabledProperty); set => SetValue(AnimationsEnabledProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public bool IsCharging { get => (bool)GetValue(IsChargingProperty); set => SetValue(IsChargingProperty, value); }
    public double FillOpacity { get => (double)GetValue(FillOpacityProperty); set => SetValue(FillOpacityProperty, value); }
    public double ColorOpacity { get => (double)GetValue(ColorOpacityProperty); set => SetValue(ColorOpacityProperty, value); }
    public double AnimationIntensity { get => (double)GetValue(AnimationIntensityProperty); set => SetValue(AnimationIntensityProperty, value); }
    private bool HasPercent => Percent is >= 0 and <= 100;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static void OnPercentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (BatteryVisual)sender;
        control.UpdateRendering();
    }

    private static void OnAnimationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (BatteryVisual)sender;
        control._phase = Now % (Math.PI * 20);
        control.UpdateRendering();
    }

    private static void OnBrushChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (BatteryVisual)sender;
        control._accentPen = control._trackPen = control._thinTrackPen = null;
        control._wavePen = null;
        control._horizontalFill = null;
    }

    private static void OnMotionModeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((BatteryVisual)sender).UpdateRendering();

    private void UpdateRendering()
    {
        if (IsLoaded && IsVisible && ColorOpacity > 0 && AnimationsEnabled &&
            (IsCharging || HasPercent && (DisplayStyle is "Bar" or "Hourglass" or "Ocean")))
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
        var now = Now;
        if (now - _lastFrame < 1d / 24) return;
        _lastFrame = now;
        _phase = now % (Math.PI * 20);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var scale = Math.Min(ActualWidth, ActualHeight) / 70;
        _layoutTransform = Frozen(new MatrixTransform(scale, 0, 0, scale,
            (ActualWidth - 70 * scale) / 2, (ActualHeight - 70 * scale) / 2));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth <= 0 || ActualHeight <= 0 || ColorOpacity <= 0) return;
        _accentPen ??= new Pen(Foreground, 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _trackPen ??= new Pen(TrackBrush, 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _thinTrackPen ??= new Pen(TrackBrush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _wavePen ??= new Pen(Foreground, .9);
        var percent = HasPercent ? Percent!.Value / 100d : 0;
        context.PushTransform(_layoutTransform);
        context.PushOpacity(ColorOpacity);
        switch (DisplayStyle)
        {
            case "Ring": DrawRing(context, percent); break;
            case "Bar": DrawBar(context, percent); break;
            case "Hourglass": DrawHourglass(context, percent); break;
            case "Fill":
                context.DrawRoundedRectangle(TrackBrush, null, new Rect(2, 13, 66, 44), 8, 8);
                CardBatteryFill.DrawSurface(context, new Rect(2, 13, 66, 44),
                    _horizontalFill ??= CardBatteryFill.CreateHorizontalBrush(Foreground), percent,
                    FillOpacity, IsCharging && AnimationsEnabled, _phase, AnimationIntensity);
                break;
            case "Charge": break;
            default: DrawOcean(context, percent); break;
        }
        if (IsCharging) DrawCharging(context);
        context.Pop();
        context.Pop();
    }

    private void DrawCharging(DrawingContext context)
    {
        var breathing = AnimationsEnabled ? (Math.Sin(_phase * 1.5) + 1) / 2 : 1;
        context.PushOpacity(1 - .08 * AnimationIntensity * (1 - breathing));
        context.PushOpacity(.3);
        context.DrawGeometry(null, ChargingOutlinePen, ChargingBolt);
        context.Pop();
        context.DrawGeometry(Foreground, null, ChargingBolt);
        context.Pop();
    }

    private void DrawRing(DrawingContext context, double percent)
    {
        context.DrawEllipse(null, _trackPen, Center, 28, 28);
        if (!HasPercent || percent <= 0) return;
        var breathing = AnimationsEnabled && IsCharging ? (Math.Sin(_phase * 1.5) + 1) / 2 : 1;
        context.PushOpacity(1 - .16 * AnimationIntensity * (1 - breathing));
        if (percent >= .9999) context.DrawEllipse(null, _accentPen, Center, 28, 28);
        else
        {
            if (_ringArc is null || _ringArcPercent != percent)
            {
                _ringArc = Arc(28, percent);
                _ringArcPercent = percent;
            }
            context.DrawGeometry(null, _accentPen, _ringArc);
        }
        context.Pop();
        var angle = percent * Math.PI * 2 - Math.PI / 2;
        var highlight = AnimationsEnabled && IsCharging ? .3 * AnimationIntensity * breathing : 0;
        context.PushOpacity(.35 + highlight / 3);
        context.DrawEllipse(Brushes.White, null,
            new Point(35 + Math.Cos(angle) * 28, 35 + Math.Sin(angle) * 28), 1 + highlight, 1 + highlight);
        context.Pop();
    }

    private void DrawBar(DrawingContext context, double percent)
    {
        context.PushOpacity(.25);
        context.DrawRoundedRectangle(TrackBrush, null, new Rect(5, 22, 55, 26), 6, 6);
        context.Pop();
        context.DrawRoundedRectangle(null, _thinTrackPen, new Rect(5, 22, 55, 26), 6, 6);
        context.DrawRoundedRectangle(TrackBrush, null, new Rect(62, 29, 3, 12), 1.5, 1.5);
        context.PushOpacity(.45);
        context.DrawGeometry(TrackBrush, null, BarSegments);
        context.Pop();
        if (!HasPercent || percent <= 0) return;
        context.PushClip(BarClip);
        context.PushClip(new RectangleGeometry(new Rect(9, 26, 47 * percent, 18)));
        context.PushOpacity(FillOpacity);
        context.DrawGeometry(Foreground, null, BarSegments);
        context.Pop();
        if (AnimationsEnabled)
        {
            var x = 2 + ((_phase * 10) % 74);
            var width = 5 * AnimationIntensity;
            context.PushOpacity(FillOpacity * (IsCharging ? .55 : .35));
            context.DrawGeometry(Brushes.White, null, Polygon(
                new Point(x - width, 25), new Point(x, 25), new Point(x - 7, 45), new Point(x - width - 7, 45)));
            context.Pop();
        }
        context.Pop();
        context.Pop();
    }

    private void DrawHourglass(DrawingContext context, double percent)
    {
        context.PushOpacity(.22);
        context.DrawGeometry(TrackBrush, null, SandVessel);
        context.Pop();
        context.DrawGeometry(null, _thinTrackPen, SandVessel);
        context.DrawLine(_trackPen, new Point(20, 8), new Point(50, 8));
        context.DrawLine(_trackPen, new Point(20, 62), new Point(50, 62));
        if (!HasPercent) return;
        // Remaining charge occupies the upper chamber; spent charge collects beneath it.
        var upperSurface = 13 + 20 * (1 - percent);
        var lowerSurface = 57 - 20 * (1 - percent);
        context.PushClip(SandVessel);
        context.PushOpacity(FillOpacity);
        if (percent > 0)
        {
            context.PushClip(new RectangleGeometry(new Rect(23, 13, 24, 22)));
            context.DrawGeometry(Foreground, null, Wave(upperSurface, _phase * .7,
                AnimationsEnabled ? Math.Min(.6 * AnimationIntensity, 5 * Math.Min(percent, 1 - percent)) : 0));
            context.Pop();
        }
        if (percent < 1)
        {
            var sway = AnimationsEnabled ? Math.Sin(_phase * .8) * .3 * AnimationIntensity : 0;
            context.DrawGeometry(Foreground, null, Polygon(new Point(23, 58), new Point(47, 58),
                new Point(45, Math.Min(57, lowerSurface + 5)), new Point(35 + sway, lowerSurface),
                new Point(25, Math.Min(57, lowerSurface + 5))));
        }
        context.Pop();
        if (AnimationsEnabled && percent > .01 && lowerSurface > 37)
        {
            context.PushOpacity(FillOpacity * .65);
            context.DrawLine(_wavePen, new Point(35, 33.5), new Point(35, lowerSurface));
            context.Pop();
            context.PushOpacity(FillOpacity);
            for (var index = 0; index < 5; index++)
            {
                var progress = (_phase * .7 + index / 5d) % 1;
                var spread = Math.Sin(_phase * 2 + index) * .3 * AnimationIntensity * progress;
                context.DrawEllipse(Foreground, null, new Point(35 + spread, 34 + (lowerSurface - 34) * progress),
                    .55 + .2 * AnimationIntensity, .85 + .2 * AnimationIntensity);
            }
            context.Pop();
        }
        context.PushOpacity(.35);
        context.DrawLine(GlintPen, new Point(27, 17), new Point(28.5, 23));
        context.DrawLine(GlintPen, new Point(27, 52), new Point(28.5, 47));
        context.Pop();
        context.Pop();
    }

    private void DrawOcean(DrawingContext context, double percent)
    {
        context.PushOpacity(.25);
        context.DrawEllipse(TrackBrush, null, Center, 26.5, 26.5);
        context.Pop();
        context.DrawEllipse(null, _thinTrackPen, Center, 29, 29);
        if (!HasPercent || percent <= 0) return;
        var surface = 61.5 - 53 * percent;
        var amplitude = AnimationsEnabled ? Math.Min(2.5 * AnimationIntensity, 25 * Math.Min(percent, 1 - percent)) : 0;
        context.PushClip(OceanClip);
        context.PushOpacity(FillOpacity * .4);
        context.DrawGeometry(Foreground, null, Wave(surface - .7, -_phase * .85, amplitude));
        context.Pop();
        var frontWave = Wave(surface, _phase, amplitude);
        context.PushOpacity(FillOpacity);
        context.DrawGeometry(Foreground, null, frontWave);
        context.Pop();
        context.PushOpacity(FillOpacity);
        context.DrawGeometry(null, _wavePen, frontWave);
        context.Pop();
        context.PushOpacity(FillOpacity * .6);
        for (var index = 0; AnimationsEnabled && index < 3; index++)
        {
            var progress = (_phase * .11 + index / 3d) % 1;
            var y = 61 - progress * 48;
            if (y <= surface + 4) continue;
            context.DrawEllipse(null, GlintPen,
                new Point(24 + index * 10 + Math.Sin(_phase + index) * 1.5 * AnimationIntensity, y),
                1 + index * .2, 1 + index * .2);
        }
        context.Pop();
        context.PushOpacity(.28);
        context.DrawEllipse(Brushes.White, null, new Point(20, 20), 2, 4);
        context.Pop();
        context.Pop();
    }

    private static Geometry Arc(double radius, double percent)
    {
        var angle = percent * Math.PI * 2 - Math.PI / 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(35, 35 - radius), false, false);
            context.ArcTo(new Point(35 + Math.Cos(angle) * radius, 35 + Math.Sin(angle) * radius),
                new Size(radius, radius), 0, percent > .5, SweepDirection.Clockwise, true, false);
        }
        return Frozen(geometry);
    }

    private static Geometry Wave(double y, double phase, double amplitude)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(3, y + Math.Sin(phase) * amplitude), true, true);
            context.BezierTo(new Point(14, y + Math.Sin(phase + 1.4) * amplitude),
                new Point(24, y + Math.Sin(phase + 2.8) * amplitude), new Point(35, y + Math.Sin(phase + 4.2) * amplitude), true, false);
            context.BezierTo(new Point(46, y + Math.Sin(phase + 5.6) * amplitude),
                new Point(56, y + Math.Sin(phase + 7) * amplitude), new Point(67, y + Math.Sin(phase + 8.4) * amplitude), true, false);
            context.LineTo(new Point(67, 67), true, false);
            context.LineTo(new Point(3, 67), true, false);
        }
        return Frozen(geometry);
    }

    private static Geometry Polygon(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            for (var index = 1; index < points.Length; index++) context.LineTo(points[index], true, false);
        }
        return Frozen(geometry);
    }

    private static Geometry CreateSegments()
    {
        var group = new GeometryGroup();
        for (var index = 0; index < 8; index++)
            group.Children.Add(new RectangleGeometry(new Rect(9.6 + index * 5.8, 27, 4, 16), 1.3, 1.3));
        return Frozen(group);
    }

    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
}
