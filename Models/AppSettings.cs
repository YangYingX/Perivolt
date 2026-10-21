using System.Globalization;
using System.Text.Json.Serialization;

namespace PowerHub.Models;

public sealed class AppSettings : IJsonOnDeserializing
{
    public string Language { get; set; } = "zh-Hans";
    public bool TaskbarEnabled { get; set; }
    public string? TaskbarDeviceId { get; set; }
    public int TaskbarLeftOffset { get; set; } = 64;
    public string Theme { get; set; } = "System";
    public string? ThemeArtworkDirectory { get; set; }
    public Dictionary<string, ThemeArtworkPreference> ThemeArtwork { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DisplayStyle { get; set; } = "Ring";
    public bool AnimationsEnabled { get; set; } = true;
    public bool ShowBatteryPercentage { get; set; } = true;
    public double BatteryColorOpacity { get; set; } = 1;
    public double BarOpacity { get; set; } = .85;
    public double HourglassOpacity { get; set; } = .85;
    public double OceanOpacity { get; set; } = .85;
    public double CardFillOpacity { get; set; } = .85;
    public double AnimationIntensity { get; set; } = 1.6;
    public List<string> DeviceOrder { get; set; } = [];
    public BatteryColorConfiguration BatteryColors { get; set; } = new();
    public int LowColorThreshold { get; set; } = 20;
    public int MediumColorThreshold { get; set; } = 60;
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
    public bool AutoReportDiagnostics { get; set; }
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
    public Dictionary<string, string> DeviceLinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Older settings have no BatteryColors property and still need their saved six-color palette migrated.
    void IJsonOnDeserializing.OnDeserializing() => BatteryColors = null!;

    public void Normalize()
    {
        Language = PowerHub.Services.L.Normalize(Language);
        ThemeArtworkDirectory = string.IsNullOrWhiteSpace(ThemeArtworkDirectory) ? null : ThemeArtworkDirectory.Trim();
        ThemeArtwork = new((ThemeArtwork ?? []).Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.OrdinalIgnoreCase);
        foreach (var artwork in ThemeArtwork.Values)
        {
            artwork.Opacity = ClampFinite(artwork.Opacity, 0, 1, .45);
            artwork.Position = artwork.Position is "Left" or "Center" ? artwork.Position : "Right";
            if (string.IsNullOrWhiteSpace(artwork.Path)) artwork.Path = null;
        }
        TaskbarLeftOffset = Math.Clamp(TaskbarLeftOffset, 0, 32767);
        if (string.IsNullOrWhiteSpace(TaskbarDeviceId)) TaskbarDeviceId = null;
        CardFillOpacity = ClampFinite(CardFillOpacity, .1, 1, .85);
        Theme = Theme?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark", "light" => "Light", "ocean" => "Ocean", "lavender" => "Lavender",
            "sunset" => "Sunset", "forest" => "Forest",
            var plugin when plugin is { Length: > 7 and <= 87 } && plugin.StartsWith("plugin:", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(plugin[7..]) => plugin,
            _ => "System"
        };
        DisplayStyle = DisplayStyle?.Trim().ToLowerInvariant() switch
        {
            "bar" => "Bar", "hourglass" => "Hourglass", "ocean" => "Ocean", "fill" => "Fill", _ => "Ring"
        };
        BarOpacity = ClampFinite(BarOpacity, .1, 1, .85);
        BatteryColorOpacity = ClampFinite(BatteryColorOpacity, 0, 1, 1);
        HourglassOpacity = ClampFinite(HourglassOpacity, .1, 1, .85);
        OceanOpacity = ClampFinite(OceanOpacity, .1, 1, .85);
        AnimationIntensity = ClampFinite(AnimationIntensity, .5, 3, 1.6);
        DeviceOrder = (DeviceOrder ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 100);
        LowColorThreshold = Math.Clamp(LowColorThreshold, 0, 99);
        MediumColorThreshold = Math.Clamp(MediumColorThreshold, LowColorThreshold + 1, 100);
        LowColorStart = NormalizeHexColor(LowColorStart) ?? "#F19AAB";
        LowColorEnd = NormalizeHexColor(LowColorEnd) ?? "#D95073";
        MediumColorStart = NormalizeHexColor(MediumColorStart) ?? "#F2C27B";
        MediumColorEnd = NormalizeHexColor(MediumColorEnd) ?? "#D59A3D";
        HighColorStart = NormalizeHexColor(HighColorStart) ?? "#8FE4C5";
        HighColorEnd = NormalizeHexColor(HighColorEnd) ?? "#39BFA0";
        BatteryColors ??= CreateLegacyColors();
        BatteryColors.Normalize();
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
                if (preference.RepeatReminderMinutes is int repeat)
                    preference.RepeatReminderMinutes = Math.Clamp(repeat, 5, 1440);
                preference.DisplayStyle = preference.DisplayStyle?.Trim().ToLowerInvariant() switch
                {
                    "ring" => "Ring", "bar" => "Bar", "hourglass" => "Hourglass", "ocean" => "Ocean", "fill" => "Fill", _ => null
                };
                preference.BatteryColors?.Normalize();
                preference.BatteryColorOpacity = NormalizeOptionalNumber(preference.BatteryColorOpacity, 0, 1);
                preference.BarOpacity = NormalizeOptionalNumber(preference.BarOpacity, .1, 1);
                preference.HourglassOpacity = NormalizeOptionalNumber(preference.HourglassOpacity, .1, 1);
                preference.OceanOpacity = NormalizeOptionalNumber(preference.OceanOpacity, .1, 1);
                preference.CardFillOpacity = NormalizeOptionalNumber(preference.CardFillOpacity, .1, 1);
                preference.AnimationIntensity = NormalizeOptionalNumber(preference.AnimationIntensity, .5, 3);
                devices[id] = preference;
            }
        }
        Devices = devices;
        DeviceLinks = new((DeviceLinks ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
            !string.IsNullOrWhiteSpace(pair.Value) && !pair.Key.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.OrdinalIgnoreCase);
        foreach (var id in DeviceLinks.Keys.ToArray())
        {
            var resolved = ResolveDeviceId(id);
            if (resolved.Equals(id, StringComparison.OrdinalIgnoreCase)) DeviceLinks.Remove(id);
            else DeviceLinks[id] = resolved;
        }
        DeviceOrder = DeviceOrder.Select(ResolveDeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (TaskbarDeviceId is { } taskbarId) TaskbarDeviceId = ResolveDeviceId(taskbarId);
    }

    public string ResolveDeviceId(string id)
    {
        var current = id;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (DeviceLinks.TryGetValue(current, out var next))
        {
            if (!visited.Add(current)) return id;
            current = next;
        }
        return current;
    }

    public bool HasLinkedConnections(string id) => DeviceLinks.Keys.Any(key => ResolveDeviceId(key).Equals(id, StringComparison.OrdinalIgnoreCase));

    public void LinkDevices(string sourceId, string targetId)
    {
        sourceId = ResolveDeviceId(sourceId); targetId = ResolveDeviceId(targetId);
        if (sourceId.Equals(targetId, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var key in DeviceLinks.Keys.Where(key => ResolveDeviceId(key).Equals(sourceId, StringComparison.OrdinalIgnoreCase)).ToArray())
            DeviceLinks[key] = targetId;
        DeviceLinks[sourceId] = targetId;
        if (Devices.TryGetValue(sourceId, out var source) && source.Pinned)
        {
            if (!Devices.TryGetValue(targetId, out var target)) Devices[targetId] = target = new();
            target.Pinned = true;
        }
        Normalize();
    }

    public void UnlinkDevice(string id)
    {
        id = ResolveDeviceId(id);
        foreach (var key in DeviceLinks.Keys.Where(key => ResolveDeviceId(key).Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray())
            DeviceLinks.Remove(key);
    }

    public int GetThreshold(string id) => Math.Clamp(
        Devices.TryGetValue(id, out var preference) && preference.LowBatteryThreshold is int threshold
            ? threshold : LowBatteryThreshold, 1, 100);

    public int GetRepeatReminderMinutes(string? id) => Math.Clamp(
        id is not null && Devices.TryGetValue(id, out var preference) && preference.RepeatReminderMinutes is int repeat
            ? repeat : RepeatReminderMinutes, 5, 1440);

    public string GetDisplayStyle(string? id) => id is not null && Devices.TryGetValue(id, out var preference) &&
        preference.DisplayStyle is { } style ? style : DisplayStyle;

    public bool GetAnimationsEnabled(string? id) => id is not null && Devices.TryGetValue(id, out var preference) &&
        preference.AnimationsEnabled is bool enabled ? enabled : AnimationsEnabled;

    public bool GetShowBatteryPercentage(string? id) => id is not null && Devices.TryGetValue(id, out var preference) &&
        preference.ShowBatteryPercentage is bool enabled ? enabled : ShowBatteryPercentage;

    public BatteryColorConfiguration GetBatteryColors(string? id) => id is not null && Devices.TryGetValue(id, out var preference) &&
        preference.BatteryColors is { } colors ? colors : (BatteryColors ??= CreateLegacyColors());

    public double GetFillOpacity(string? id, string style)
    {
        DevicePreference? preference = null;
        if (id is not null) Devices.TryGetValue(id, out preference);
        return style switch
        {
            "Bar" => ClampFinite(preference?.BarOpacity ?? BarOpacity, .1, 1, .85),
            "Hourglass" => ClampFinite(preference?.HourglassOpacity ?? HourglassOpacity, .1, 1, .85),
            "Ocean" => ClampFinite(preference?.OceanOpacity ?? OceanOpacity, .1, 1, .85),
            "Fill" => ClampFinite(preference?.CardFillOpacity ?? CardFillOpacity, .1, 1, .85),
            _ => 1
        };
    }

    public double GetAnimationIntensity(string? id) => ClampFinite(
        id is not null && Devices.TryGetValue(id, out var preference) && preference.AnimationIntensity is double intensity
            ? intensity : AnimationIntensity, .5, 3, 1.6);

    public double GetBatteryColorOpacity(string? id) => ClampFinite(
        id is not null && Devices.TryGetValue(id, out var preference) && preference.BatteryColorOpacity is double opacity
            ? opacity : BatteryColorOpacity, 0, 1, 1);

    public int GetDeviceOrder(string id)
    {
        var index = DeviceOrder.FindIndex(saved => string.Equals(saved, id, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static double? NormalizeOptionalNumber(double? value, double minimum, double maximum) =>
        value is double number && double.IsFinite(number) ? Math.Clamp(number, minimum, maximum) : null;

    private BatteryColorConfiguration CreateLegacyColors()
    {
        var colors = new BatteryColorConfiguration
        {
            GradientByBatteryLevel = true,
            GradientWithinShape = BatteryGradientEnabled,
            Stops =
            [
                new() { Position = 0, Color = LowColorStart, InterpolateToNext = BatteryGradientEnabled },
                new() { Position = LowColorThreshold, Color = LowColorEnd, InterpolateToNext = false },
                new() { Position = LowColorThreshold + 1, Color = MediumColorStart, InterpolateToNext = BatteryGradientEnabled },
                new() { Position = MediumColorThreshold, Color = MediumColorEnd, InterpolateToNext = false },
                new() { Position = Math.Min(100, MediumColorThreshold + 1), Color = HighColorStart, InterpolateToNext = BatteryGradientEnabled },
                new() { Position = 100, Color = HighColorEnd }
            ]
        };
        colors.Normalize();
        return colors;
    }

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
    public int? RepeatReminderMinutes { get; set; }
    public string? DisplayStyle { get; set; }
    public bool? AnimationsEnabled { get; set; }
    public bool? ShowBatteryPercentage { get; set; }
    public double? BatteryColorOpacity { get; set; }
    public double? BarOpacity { get; set; }
    public double? HourglassOpacity { get; set; }
    public double? OceanOpacity { get; set; }
    public double? CardFillOpacity { get; set; }
    public double? AnimationIntensity { get; set; }
    public BatteryColorConfiguration? BatteryColors { get; set; }
}

public sealed class BatteryColorConfiguration
{
    public bool GradientByBatteryLevel { get; set; } = true;
    public bool GradientWithinShape { get; set; } = true;
    public List<BatteryColorStop> Stops { get; set; } =
    [
        new() { Position = 20, Color = "#F19AAB" },
        new() { Position = 60, Color = "#F2C27B" },
        new() { Position = 100, Color = "#8FE4C5" }
    ];

    public BatteryColorConfiguration Clone() => new()
    {
        GradientByBatteryLevel = GradientByBatteryLevel,
        GradientWithinShape = GradientWithinShape,
        Stops = (Stops ?? []).Where(stop => stop is not null).Select(stop => new BatteryColorStop
        {
            Position = stop.Position, Color = stop.Color, InterpolateToNext = stop.InterpolateToNext
        }).ToList()
    };

    public void Normalize()
    {
        var stops = (Stops ?? []).Where(stop => stop is not null).ToList();
        foreach (var stop in stops)
        {
            stop.Position = double.IsFinite(stop.Position) ? Math.Clamp(stop.Position, 0, 100) : 0;
            stop.Color = AppSettings.NormalizeHexColor(stop.Color) ?? "#8FE4C5";
        }
        stops = stops.OrderBy(stop => stop.Position).Take(10).ToList();
        if (stops.Count == 0) stops = new BatteryColorConfiguration().Stops;
        if (stops.Count == 1) stops.Add(new BatteryColorStop
        {
            Position = stops[0].Position <= 50 ? 100 : 0, Color = stops[0].Color,
            InterpolateToNext = stops[0].InterpolateToNext
        });
        Stops = stops.OrderBy(stop => stop.Position).ToList();
    }
}

public sealed class BatteryColorStop
{
    public double Position { get; set; }
    public string Color { get; set; } = "#8FE4C5";
    public bool InterpolateToNext { get; set; } = true;
}
