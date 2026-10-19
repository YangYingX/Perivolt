using System.Globalization;

namespace PowerHub.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public string DisplayStyle { get; set; } = "Ocean";
    public bool AnimationsEnabled { get; set; } = true;
    public int LowColorThreshold { get; set; } = 20;
    public int MediumColorThreshold { get; set; } = 50;
    public string LowColorStart { get; set; } = "#F19AAB";
    public string LowColorEnd { get; set; } = "#D95073";
    public string MediumColorStart { get; set; } = "#F2C27B";
    public string MediumColorEnd { get; set; } = "#D59A3D";
    public string HighColorStart { get; set; } = "#8FE4C5";
    public string HighColorEnd { get; set; } = "#39BFA0";
    public bool BatteryGradientEnabled { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
    public int RepeatReminderMinutes { get; set; } = 60;
    public bool NotificationsEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 120;
    public bool StartInTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutoCheckUpdates { get; set; }
    public Dictionary<string, bool> EnabledPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> PluginOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool WidgetEnabled { get; set; }
    public bool WidgetTopmost { get; set; }
    public bool WidgetLocked { get; set; }
    public bool WidgetCompact { get; set; }
    public bool WidgetTransparent { get; set; }
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public bool HistoryEnabled { get; set; }
    public Dictionary<string, DevicePreference> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        Theme = Theme?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark", "light" => "Light", "ocean" => "Ocean", "lavender" => "Lavender",
            "sunset" => "Sunset", "forest" => "Forest", _ => "System"
        };
        DisplayStyle = DisplayStyle?.Trim().ToLowerInvariant() switch
        {
            "ring" => "Ring", "bar" => "Bar", "hourglass" => "Hourglass", _ => "Ocean"
        };
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 100);
        LowColorThreshold = Math.Clamp(LowColorThreshold, 0, 99);
        MediumColorThreshold = Math.Clamp(MediumColorThreshold, LowColorThreshold + 1, 100);
        LowColorStart = NormalizeHexColor(LowColorStart) ?? "#F19AAB";
        LowColorEnd = NormalizeHexColor(LowColorEnd) ?? "#D95073";
        MediumColorStart = NormalizeHexColor(MediumColorStart) ?? "#F2C27B";
        MediumColorEnd = NormalizeHexColor(MediumColorEnd) ?? "#D59A3D";
        HighColorStart = NormalizeHexColor(HighColorStart) ?? "#8FE4C5";
        HighColorEnd = NormalizeHexColor(HighColorEnd) ?? "#39BFA0";
        RepeatReminderMinutes = Math.Clamp(RepeatReminderMinutes, 5, 1440);
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, 30, 900);
        if (WidgetLeft is double left && !double.IsFinite(left)) WidgetLeft = null;
        if (WidgetTop is double top && !double.IsFinite(top)) WidgetTop = null;
        EnabledPlugins = new(EnabledPlugins ?? [], StringComparer.OrdinalIgnoreCase);
        PluginOptions = new((PluginOptions ?? []).Where(x => x.Value is not null)
            .ToDictionary(x => x.Key, x => x.Value), StringComparer.OrdinalIgnoreCase);

        var devices = new Dictionary<string, DevicePreference>(StringComparer.OrdinalIgnoreCase);
        if (Devices is not null)
        {
            foreach (var (id, preference) in Devices)
            {
                if (string.IsNullOrWhiteSpace(id) || preference is null) continue;
                preference.DisplayName = (preference.DisplayName ?? "").Trim();
                preference.CustomName = NormalizeCustomName(preference.CustomName);
                preference.NameColor = NormalizeHexColor(preference.NameColor);
                if (preference.DisplayName.Length > 256) preference.DisplayName = preference.DisplayName[..256];
                if (preference.LowBatteryThreshold is int threshold)
                    preference.LowBatteryThreshold = Math.Clamp(threshold, 1, 100);
                devices[id] = preference;
            }
        }
        Devices = devices;
    }

    public int GetThreshold(string id) => Math.Clamp(
        Devices.TryGetValue(id, out var preference) && preference.LowBatteryThreshold is int threshold
            ? threshold : LowBatteryThreshold, 1, 100);

    public bool IsPinned(string id) => Devices.TryGetValue(id, out var preference) && preference.Pinned;

    public bool IsDeviceNotificationEnabled(string id) => NotificationsEnabled &&
        (!Devices.TryGetValue(id, out var preference) || preference.NotificationsEnabled);

    public string GetDeviceName(DeviceStatus device) => Devices.TryGetValue(device.Id, out var preference) &&
        !string.IsNullOrWhiteSpace(preference.CustomName) ? preference.CustomName : device.Name;

    internal static string? NormalizeCustomName(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length <= 80) return value;
        var length = char.IsHighSurrogate(value[79]) ? 79 : 80;
        return value[..length];
    }

    internal static string? NormalizeHexColor(string? value)
    {
        value = value?.Trim();
        return value is { Length: 7 } && value[0] == '#' &&
            uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)
            ? value.ToUpperInvariant() : null;
    }
}

public sealed class DevicePreference
{
    public string DisplayName { get; set; } = "";
    public string? CustomName { get; set; }
    public string? NameColor { get; set; }
    public bool Pinned { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public int? LowBatteryThreshold { get; set; }
}
