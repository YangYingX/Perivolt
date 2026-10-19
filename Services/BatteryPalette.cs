using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PowerHub.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace PowerHub.Services;

public static class BatteryPalette
{
    private static BatteryColorConfiguration defaultColors = new AppSettings().GetBatteryColors(null);
    private static readonly ConditionalWeakTable<BatteryColorConfiguration, Palette> Palettes = new();

    public static void Apply(AppSettings settings) => defaultColors = settings.GetBatteryColors(null);

    public static Brush GetBrush(int? percent) => GetBrush(defaultColors, percent);

    public static Brush GetBrush(BatteryColorConfiguration colors, int? percent)
    {
        if (percent is not (>= 0 and <= 100))
            return System.Windows.Application.Current?.TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;
        return GetPalette(colors).GetBrush(percent.Value);
    }

    public static Brush GetScaleBrush(BatteryColorConfiguration colors) => GetPalette(colors).GetScaleBrush();

    private static Palette GetPalette(BatteryColorConfiguration colors)
    {
        var palette = Palettes.GetValue(colors, configuration => new Palette(configuration));
        if (!palette.Matches(colors))
        {
            Palettes.Remove(colors);
            palette = Palettes.GetValue(colors, configuration => new Palette(configuration));
        }
        return palette;
    }

    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#' ||
            !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private sealed class Palette
    {
        private readonly BatteryColorConfiguration original;
        private readonly BatteryColorConfiguration normalized;
        private readonly Color[] colors;
        private readonly Brush?[] brushes = new Brush[101];
        private Brush? scaleBrush;

        public Palette(BatteryColorConfiguration configuration)
        {
            original = configuration.Clone();
            normalized = configuration.Clone();
            normalized.Normalize();
            colors = normalized.Stops.Select(stop =>
            {
                TryParseColor(stop.Color, out var color);
                return color;
            }).ToArray();
        }

        public bool Matches(BatteryColorConfiguration configuration) =>
            original.GradientByBatteryLevel == configuration.GradientByBatteryLevel &&
            original.GradientWithinShape == configuration.GradientWithinShape && configuration.Stops is not null &&
            original.Stops.Count == configuration.Stops.Count && original.Stops.Zip(configuration.Stops).All(pair =>
                pair.Second is not null && pair.First.Position.Equals(pair.Second.Position) && pair.First.Color == pair.Second.Color &&
                pair.First.InterpolateToNext == pair.Second.InterpolateToNext);

        public Brush GetBrush(int percent)
        {
            if (brushes[percent] is { } cached) return cached;
            Brush brush;
            var right = FindRightIndex(percent);
            var limit = normalized.GradientByBatteryLevel ? percent : 100;
            if (right == 0 || percent > normalized.Stops[^1].Position ||
                !normalized.Stops[right - 1].InterpolateToNext)
                brush = new SolidColorBrush(colors[right]);
            else if (!normalized.GradientWithinShape || limit == 0)
                brush = new SolidColorBrush(ColorAt(percent, normalized.GradientByBatteryLevel));
            else
                brush = CreateGradientBrush(limit, false);
            brush.Freeze();
            brushes[percent] = brush;
            return brush;
        }

        public Brush GetScaleBrush()
        {
            if (scaleBrush is not null) return scaleBrush;
            scaleBrush = CreateGradientBrush(100, true,
                !normalized.GradientByBatteryLevel && !normalized.GradientWithinShape);
            scaleBrush.Freeze();
            return scaleBrush;
        }

        private LinearGradientBrush CreateGradientBrush(double limit, bool horizontal, bool forceSteps = false)
        {
            var stops = new GradientStopCollection { new(ColorAt(0, true), 0) };
            for (var index = 0; index < normalized.Stops.Count; index++)
            {
                var stop = normalized.Stops[index];
                if (stop.Position > limit) break;
                var offset = stop.Position / limit;
                stops.Add(new GradientStop(colors[index], offset));
                if (index + 1 < normalized.Stops.Count && (forceSteps || !stop.InterpolateToNext) && stop.Position < limit)
                    stops.Add(new GradientStop(colors[index + 1], offset));
            }
            stops.Add(new GradientStop(ColorAt(limit, true), 1));
            return new LinearGradientBrush(stops, horizontal ? new Point(0, 0) : new Point(0, 1),
                horizontal ? new Point(1, 0) : new Point(0, 0));
        }

        private int FindRightIndex(double percent)
        {
            var right = 0;
            while (right + 1 < normalized.Stops.Count && normalized.Stops[right].Position < percent) right++;
            return right;
        }

        private Color ColorAt(double percent, bool interpolate)
        {
            var next = FindRightIndex(percent);
            if (next == 0 || percent >= normalized.Stops[^1].Position ||
                !interpolate || !normalized.Stops[next - 1].InterpolateToNext) return colors[next];
            var left = next - 1;
            var amount = Math.Clamp((percent - normalized.Stops[left].Position) /
                (normalized.Stops[next].Position - normalized.Stops[left].Position), 0, 1);
            var from = colors[left];
            var to = colors[next];
            return Color.FromRgb((byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }
    }
}
