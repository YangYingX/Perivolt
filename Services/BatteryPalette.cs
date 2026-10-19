using System.Globalization;
using System.Windows.Media;
using PowerHub.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace PowerHub.Services;

public static class BatteryPalette
{
    private readonly record struct PaletteConfiguration(int Low, int Medium, bool Gradient,
        string LowStart, string LowEnd, string MediumStart, string MediumEnd, string HighStart, string HighEnd);

    private static PaletteConfiguration? _configuration;
    private static int _low = 20;
    private static int _medium = 50;
    private static Brush _lowBrush = CreateBrush("#F19AAB", "#D95073", true);
    private static Brush _mediumBrush = CreateBrush("#F2C27B", "#D59A3D", true);
    private static Brush _highBrush = CreateBrush("#8FE4C5", "#39BFA0", true);

    public static void Apply(AppSettings settings)
    {
        var next = new PaletteConfiguration(settings.LowColorThreshold, settings.MediumColorThreshold,
            settings.BatteryGradientEnabled, settings.LowColorStart, settings.LowColorEnd,
            settings.MediumColorStart, settings.MediumColorEnd, settings.HighColorStart, settings.HighColorEnd);
        if (_configuration == next) return;
        _low = Math.Clamp(next.Low, 0, 99);
        _medium = Math.Clamp(next.Medium, _low + 1, 100);
        _lowBrush = CreateBrush(next.LowStart, next.LowEnd, next.Gradient);
        _mediumBrush = CreateBrush(next.MediumStart, next.MediumEnd, next.Gradient);
        _highBrush = CreateBrush(next.HighStart, next.HighEnd, next.Gradient);
        _configuration = next;
    }

    public static Brush GetBrush(int? percent) => percent is not (>= 0 and <= 100)
        ? System.Windows.Application.Current?.TryFindResource("TextSecondary") as Brush ?? Brushes.Gray
        : percent <= _low ? _lowBrush : percent <= _medium ? _mediumBrush : _highBrush;

    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#' ||
            !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static Brush CreateBrush(string start, string end, bool gradient)
    {
        if (!TryParseColor(start, out var first)) first = Colors.Gray;
        if (!TryParseColor(end, out var last)) last = first;
        Brush brush = gradient ? new LinearGradientBrush(first, last, new Point(0, 1), new Point(1, 0)) : new SolidColorBrush(first);
        brush.Freeze();
        return brush;
    }
}
