using System.Globalization;
using PowerHub.Plugins;

namespace PowerHub.Extensions;

public sealed class CharacterThemesPlugin : IPowerHubPlugin
{
    public IReadOnlyList<PluginTheme> Themes { get; } = Load();

    private static IReadOnlyList<PluginTheme> Load()
    {
        using var stream = typeof(CharacterThemesPlugin).Assembly.GetManifestResourceStream("PowerHub.CharacterThemes.Themes.txt")!;
        using var reader = new StreamReader(stream);
        var themes = new List<PluginTheme>(200);
        string game = "", prefix = "", motif = "";
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var fields = line.Split('|');
            if (line.StartsWith('@')) { prefix = fields[0][1..]; game = fields[1]; motif = fields[2]; continue; }
            var dark = fields[3] == "D";
            themes.Add(new(prefix + "-" + fields[0], dark, Palette(fields[4], fields[5], fields[6], dark))
            {
                Name = fields[1], Group = game, Description = fields[2], Motif = motif
            });
        }
        return themes.AsReadOnly();
    }

    private static IReadOnlyDictionary<string, string> Palette(string primary, string secondary, string basis, bool dark)
    {
        var page = Mix(basis, dark ? "#080A12" : "#FFFFFF", dark ? .40 : .94);
        var surface = Mix(basis, dark ? "#080A12" : "#FFFFFF", dark ? .16 : .985);
        var surfaceAlt = Mix(surface, primary, dark ? .10 : .07);
        var accent = Readable(primary, [page, surface, surfaceAlt], dark);
        var soft = Mix(surface, accent, dark ? .16 : .10);
        accent = Readable(accent, [page, surface, surfaceAlt, soft], dark);
        var stroke = Mix(surface, secondary, dark ? .32 : .22);
        return new Dictionary<string, string>
        {
            ["PageBackground"] = page, ["Surface"] = surface, ["SurfaceAlt"] = surfaceAlt,
            ["Stroke"] = stroke, ["TextPrimary"] = dark ? "#F6F7FC" : Mix(basis, "#10121C", .75),
            ["TextSecondary"] = Readable(secondary, [page, surface, surfaceAlt, soft], dark),
            ["Accent"] = accent, ["AccentSoft"] = soft,
            ["AccentText"] = Contrast(accent, "#FFFFFF") > Contrast(accent, "#10121C") ? "#FFFFFF" : "#10121C",
            ["Warning"] = dark ? "#F6CC84" : "#925F10", ["Danger"] = dark ? "#FF9EB1" : "#B82B49",
            ["RingTrack"] = stroke
        };
    }

    private static string Readable(string color, string[] backgrounds, bool dark)
    {
        for (var step = 0; step < 32 && backgrounds.Any(background => Contrast(color, background) < 4.5); step++)
            color = Mix(color, dark ? "#FFFFFF" : "#080A12", .10);
        return color;
    }

    private static string Mix(string left, string right, double amount)
    {
        var a = Rgb(left); var b = Rgb(right);
        return "#" + string.Concat(a.Zip(b, (x, y) => ((int)Math.Round(x + (y - x) * amount)).ToString("X2", CultureInfo.InvariantCulture)));
    }
    private static int[] Rgb(string color) => [Convert.ToInt32(color[1..3], 16), Convert.ToInt32(color[3..5], 16), Convert.ToInt32(color[5..7], 16)];
    private static double Luminance(string color)
    {
        var rgb = Rgb(color).Select(value => value / 255d).Select(value => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4)).ToArray();
        return rgb[0] * .2126 + rgb[1] * .7152 + rgb[2] * .0722;
    }
    private static double Contrast(string left, string right)
    {
        var a = Luminance(left); var b = Luminance(right);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
