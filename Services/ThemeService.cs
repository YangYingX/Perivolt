using Microsoft.Win32;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PowerHub.Services;

public sealed class ThemeService : IDisposable
{
    private string mode = "System";
    private bool initialized;
    private PowerHub.Plugins.PluginTheme? extension;
    public bool IsDark { get; private set; }
    public event Action? SystemThemeChanged;

    public ThemeService() => SystemEvents.UserPreferenceChanged += OnPreferenceChanged;

    public void Apply(string theme, PowerHub.Plugins.PluginTheme? pluginTheme = null)
    {
        if (!theme.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase)) pluginTheme = null;
        var darkMode = theme is "Dark" or "Ocean" or "Forest" || theme == "System" && !SystemUsesLightTheme();
        if (pluginTheme is not null) darkMode = pluginTheme.IsDark;
        if (initialized && mode == theme && IsDark == darkMode && ReferenceEquals(extension, pluginTheme)) return;
        extension = pluginTheme;
        mode = theme;
        IsDark = darkMode;
        initialized = true;
        var dark = new Dictionary<string, string>
        {
            ["PageBackground"] = "#0E121B", ["Surface"] = "#171D29", ["SurfaceAlt"] = "#202838",
            ["Stroke"] = "#2B3546", ["TextPrimary"] = "#F0F4FA", ["TextSecondary"] = "#A2AEC1",
            ["Accent"] = "#8FE4C5", ["AccentSoft"] = "#203B34", ["AccentText"] = "#102C23",
            ["Warning"] = "#F2C27B", ["Danger"] = "#F19AAB", ["RingTrack"] = "#303A4C"
        };
        var light = new Dictionary<string, string>
        {
            ["PageBackground"] = "#F3F5F9", ["Surface"] = "#FFFFFF", ["SurfaceAlt"] = "#EDF1F6",
            ["Stroke"] = "#E1E6EF", ["TextPrimary"] = "#18253B", ["TextSecondary"] = "#697990",
            ["Accent"] = "#14866B", ["AccentSoft"] = "#E0F3EB", ["AccentText"] = "#FFFFFF",
            ["Warning"] = "#AE701A", ["Danger"] = "#C94D67", ["RingTrack"] = "#E6EBF2"
        };
        var palette = IsDark ? dark : light;
        var colors = theme switch
        {
            "Ocean" => new[] { "#0C1727", "#13233A", "#1C304A", "#2B4562", "#EAF4FF", "#9FB9D1", "#78C9FF", "#203E5B", "#102A40", "#2B4562" },
            "Lavender" => new[] { "#F3F0FA", "#FCFAFF", "#EEE8F6", "#E0D6ED", "#352C49", "#81728F", "#8560B3", "#EDE2F8", "#FFFFFF", "#E5DCEE" },
            "Sunset" => new[] { "#FBF2EC", "#FFFCF9", "#F6E8DE", "#EEDCCF", "#493429", "#927866", "#B76B49", "#F8E2D2", "#FFFFFF", "#EFDFD3" },
            "Forest" => new[] { "#101D1A", "#192C25", "#243B31", "#355044", "#EFF8ED", "#A5C0AC", "#B8DC91", "#314A2D", "#23361C", "#355044" },
            _ => null
        };
        if (colors is not null)
        {
            var keys = new[] { "PageBackground", "Surface", "SurfaceAlt", "Stroke", "TextPrimary", "TextSecondary", "Accent", "AccentSoft", "AccentText", "RingTrack" };
            for (var index = 0; index < keys.Length; index++) palette[keys[index]] = colors[index];
        }
        if (pluginTheme is not null)
            foreach (var (key, value) in pluginTheme.Colors)
                if (palette.ContainsKey(key) && BatteryPalette.TryParseColor(value, out _)) palette[key] = value;
        foreach (var entry in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.Value));
            brush.Freeze();
            System.Windows.Application.Current.Resources[entry.Key] = brush;
        }
        System.Windows.Application.Current.Resources["ThemeDecoration"] = Controls.ThemeArtwork.Decoration(pluginTheme);
    }

    private static bool SystemUsesLightTheme()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not int value || value != 0; }
        catch (System.Security.SecurityException) { return true; }
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (mode == "System") System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            var previous = IsDark;
            Apply(mode, extension);
            if (previous != IsDark) SystemThemeChanged?.Invoke();
        }));
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
}
