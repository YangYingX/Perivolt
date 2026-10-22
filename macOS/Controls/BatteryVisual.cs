using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PowerHub.Models;
using PowerHub.Services;

namespace PowerHub.Mac.Controls;

// Uses the same saved palette and per-device appearance as the Windows application.
public sealed class BatteryVisual : Control
{
    private readonly AppSettings settings;
    private readonly string? deviceId;
    private DeviceStatus device;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1d / 24) };
    private TopLevel? root;
    private double phase;

    public BatteryVisual(AppSettings settings, string? deviceId, DeviceStatus device)
    {
        this.settings = settings;
        this.deviceId = deviceId;
        this.device = device;
        Width = 94; Height = 76;
        timer.Tick += (_, _) =>
        {
            if (!ShouldAnimate()) { timer.Stop(); return; }
            phase = (phase + .08) % (Math.PI * 2);
            InvalidateVisual();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property.Name is nameof(IsVisible) or nameof(IsEffectivelyVisible)) UpdateAnimation();
        };
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        AttachedToVisualTree += (_, _) =>
        {
            root = TopLevel.GetTopLevel(this);
            if (root is not null) root.PropertyChanged += RootChanged;
            UpdateAnimation();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            if (root is not null) root.PropertyChanged -= RootChanged;
            root = null;
        };
    }

    public void UpdateDevice(DeviceStatus value)
    {
        device = value;
        UpdateAnimation();
        InvalidateVisual();
    }

    private void RootChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name is nameof(IsVisible) or nameof(Window.WindowState)) UpdateAnimation();
    }

    private bool ShouldAnimate() => root?.IsVisible == true && IsEffectivelyVisible
        && root is not Window { WindowState: WindowState.Minimized }
        && settings.GetAnimationsEnabled(deviceId) && device.BatteryPercent is > 0 and < 100
        && settings.GetDisplayStyle(deviceId) is "Hourglass" or "Ocean" or "Fill";

    private void UpdateAnimation()
    {
        if (ShouldAnimate()) timer.Start();
        else timer.Stop();
    }

    public static Color ColorAt(BatteryColorConfiguration palette, double percent)
    {
        var stops = palette.Stops;
        if (stops.Count == 0) return Color.Parse("#8FE4C5");
        percent = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        var right = 0;
        while (right + 1 < stops.Count && stops[right].Position < percent) right++;
        var current = Color.Parse(stops[right].Color);
        if (right == 0 || !palette.GradientByBatteryLevel || !stops[right - 1].InterpolateToNext || percent >= stops[^1].Position) return current;
        var from = Color.Parse(stops[right - 1].Color);
        var range = stops[right].Position - stops[right - 1].Position;
        var amount = range <= 0 ? 1 : Math.Clamp((percent - stops[right - 1].Position) / range, 0, 1);
        return Color.FromRgb((byte)Math.Round(from.R + (current.R - from.R) * amount),
            (byte)Math.Round(from.G + (current.G - from.G) * amount), (byte)Math.Round(from.B + (current.B - from.B) * amount));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        UpdateAnimation();
        if (Bounds.Width < 20 || Bounds.Height < 20) return;
        var knownPercent = device.BatteryPercent is >= 0 and <= 100;
        var percent = knownPercent ? device.BatteryPercent!.Value : 0;
        var fraction = percent / 100d;
        var style = settings.GetDisplayStyle(deviceId);
        var palette = settings.GetBatteryColors(deviceId);
        var theme = MacThemePalette.Create(settings.Theme);
        var brush = CreateFillBrush(palette, percent, knownPercent, theme.Muted);
        var outline = new Pen(theme.Stroke, 2.5);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var body = new Rect(8, 8, Bounds.Width - 16, Bounds.Height - 16);
        var showText = settings.GetShowBatteryPercentage(deviceId);
        var intensity = settings.GetAnimationIntensity(deviceId);
        var animate = ShouldAnimate();
        var label = knownPercent || device.BatteryPercent is null ? device.BatteryText : L.T("--");
        var fontSize = style == "Hourglass" ? 14 : 17;
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(MacThemePalette.InterfaceFont), fontSize, theme.Text);
        var textPosition = new Point(center.X - text.Width / 2, center.Y - text.Height / 2);
        var textOnFill = false;

        using (context.PushOpacity(settings.GetBatteryColorOpacity(deviceId)))
        {
            if (style == "Ring")
            {
                var radius = Math.Max(1, Math.Min(body.Width, body.Height) / 2 - 3);
                context.DrawEllipse(null, outline, center, radius, radius);
                if (knownPercent && percent == 100)
                    context.DrawEllipse(null, new Pen(brush, 6), center, radius, radius);
                else if (knownPercent && fraction > 0)
                {
                    var geometry = new StreamGeometry();
                    using (var path = geometry.Open())
                    {
                        path.BeginFigure(new Point(center.X, center.Y - radius), false);
                        var angle = fraction * Math.PI * 2;
                        path.ArcTo(new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius),
                            new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise);
                    }
                    context.DrawGeometry(null, new Pen(brush, 6, lineCap: PenLineCap.Round), geometry);
                }
            }
            else if (style == "Bar")
            {
                var height = Math.Min(24, body.Height * .42);
                var bar = new Rect(body.X, center.Y - height * (showText ? .75 : .5), Math.Max(4, body.Width - 5), height);
                context.DrawRectangle(null, outline, bar, 6, 6);
                if (knownPercent && fraction > 0)
                    using (context.PushOpacity(settings.GetFillOpacity(deviceId, style)))
                        context.DrawRectangle(brush, null, new Rect(bar.X + 3, bar.Y + 3, Math.Max(0, (bar.Width - 6) * fraction), Math.Max(0, bar.Height - 6)), 3, 3);
                context.DrawRectangle(theme.Stroke, null, new Rect(bar.Right + 1, bar.Y + height * .3, 3, height * .4), 1, 1);
                textPosition = new Point(center.X - text.Width / 2, Math.Min(body.Bottom - text.Height + 2, bar.Bottom + 5));
            }
            else if (style == "Hourglass")
            {
                var textSpace = showText ? Math.Min(body.Width * .46, Math.Max(28, text.Width + 4)) : 0;
                var area = new Rect(body.X, body.Y, Math.Max(8, body.Width - textSpace - (showText ? 5 : 0)), body.Height);
                var glassWidth = Math.Max(4, Math.Min(area.Width - 2, area.Height * .68));
                var glass = new Rect(area.Center.X - glassWidth / 2, area.Y + 2, glassWidth, Math.Max(2, area.Height - 4));
                var neck = glass.Center.Y;
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(glass.TopLeft, true);
                    path.LineTo(glass.TopRight);
                    path.LineTo(new Point(glass.Center.X + glass.Width * .08, neck));
                    path.LineTo(glass.BottomRight);
                    path.LineTo(glass.BottomLeft);
                    path.LineTo(new Point(glass.Center.X - glass.Width * .08, neck));
                    path.EndFigure(true);
                }
                if (knownPercent)
                    using (context.PushGeometryClip(geometry))
                    using (context.PushOpacity(settings.GetFillOpacity(deviceId, style)))
                    {
                        var chamber = glass.Height * .47;
                        context.DrawRectangle(brush, null, new Rect(glass.X, neck - chamber * fraction, glass.Width, chamber * fraction));
                        context.DrawRectangle(brush, null, new Rect(glass.X, glass.Bottom - chamber * (1 - fraction), glass.Width, chamber * (1 - fraction)));
                        if (animate)
                            for (var grain = 0; grain < 4; grain++)
                                context.DrawEllipse(brush, null, new Point(glass.Center.X + Math.Sin(phase + grain) * intensity * .4,
                                    neck + (phase * 2.5 + grain * .25) % 1 * chamber), 1, 1);
                    }
                context.DrawGeometry(null, outline, geometry);
                textPosition = new Point(Math.Min(body.Right - text.Width, glass.Right + 5), center.Y - text.Height / 2);
            }
            else if (style == "Ocean")
            {
                var radius = Math.Max(1, Math.Min(body.Width, body.Height) / 2 - 1);
                var circle = new EllipseGeometry(new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
                if (knownPercent && fraction > 0)
                    using (context.PushGeometryClip(circle))
                    using (context.PushOpacity(settings.GetFillOpacity(deviceId, style)))
                    {
                        var surface = center.Y + radius - radius * 2 * fraction;
                        var wave = new StreamGeometry();
                        using (var path = wave.Open())
                        {
                            path.BeginFigure(new Point(center.X - radius, center.Y + radius), true);
                            for (var x = -radius; x <= radius + 2; x += 2)
                            {
                                var dy = animate ? Math.Sin(x / 10 + phase) * 1.5 * intensity : 0;
                                path.LineTo(new Point(center.X + Math.Min(x, radius), Math.Clamp(surface + dy, center.Y - radius, center.Y + radius)));
                            }
                            path.LineTo(new Point(center.X + radius, center.Y + radius)); path.EndFigure(true);
                        }
                        context.DrawGeometry(brush, null, wave);
                    }
                context.DrawGeometry(null, outline, circle);
                textOnFill = true;
            }
            else
            {
                if (knownPercent && fraction > 0)
                    using (context.PushClip(new RoundedRect(body, 10)))
                    using (context.PushOpacity(settings.GetFillOpacity(deviceId, style)))
                    {
                        var fill = new StreamGeometry();
                        using (var path = fill.Open())
                        {
                            path.BeginFigure(body.TopLeft, true);
                            var edge = body.X + body.Width * fraction;
                            for (var y = body.Top; y <= body.Bottom + 2; y += 2)
                            {
                                var dx = animate ? Math.Sin((y - body.Top) / 9 + phase) * 1.5 * intensity : 0;
                                path.LineTo(new Point(Math.Clamp(edge + dx, body.Left, body.Right), Math.Min(y, body.Bottom)));
                            }
                            path.LineTo(body.BottomLeft); path.EndFigure(true);
                        }
                        context.DrawGeometry(brush, null, fill);
                    }
                context.DrawRectangle(null, outline, body, 10, 10);
                textOnFill = true;
            }
        }
        if (!showText) return;
        if (textOnFill)
            using (context.PushOpacity(.94))
                context.DrawRectangle(theme.Surface, null, new Rect(textPosition.X - 5, textPosition.Y - 2, text.Width + 10, text.Height + 4), 6, 6);
        context.DrawText(text, textPosition);
    }

    private static IBrush CreateFillBrush(BatteryColorConfiguration palette, double percent, bool knownPercent, IBrush unknown)
    {
        if (!knownPercent) return unknown;
        IBrush brush = new SolidColorBrush(ColorAt(palette, percent));
        if (!palette.GradientWithinShape || palette.Stops.Count < 2 || !palette.Stops.Any(stop => stop.InterpolateToNext)) return brush;
        var limit = palette.GradientByBatteryLevel ? Math.Max(1, percent) : 100;
        var gradient = new LinearGradientBrush { StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative) };
        gradient.GradientStops.Add(new GradientStop(ColorAt(palette, 0), 0));
        for (var index = 0; index < palette.Stops.Count; index++)
        {
            var stop = palette.Stops[index];
            if (stop.Position > limit) break;
            var offset = stop.Position / limit;
            gradient.GradientStops.Add(new GradientStop(Color.Parse(stop.Color), offset));
            if (!stop.InterpolateToNext && index + 1 < palette.Stops.Count && stop.Position < limit)
                gradient.GradientStops.Add(new GradientStop(Color.Parse(palette.Stops[index + 1].Color), offset));
        }
        gradient.GradientStops.Add(new GradientStop(ColorAt(palette, limit), 1));
        return gradient;
    }
}
