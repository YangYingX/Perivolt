using System.Globalization;
using PowerHub.Plugins;
using PowerHub.Services;

namespace PowerHub.Extensions;

public sealed class AuroraThemePlugin : IPowerHubPlugin
{
    public PluginTheme Theme { get; } = new("aurora-theme", true, new Dictionary<string, string>
    {
        ["PageBackground"] = "#101422", ["Surface"] = "#191F32", ["SurfaceAlt"] = "#252D44",
        ["Stroke"] = "#35405B", ["TextPrimary"] = "#EFF4FF", ["TextSecondary"] = "#ADBAD5",
        ["Accent"] = "#A6E8D5", ["AccentSoft"] = "#28423F", ["AccentText"] = "#16332D",
        ["Warning"] = "#F2CB91", ["Danger"] = "#F3A5B9", ["RingTrack"] = "#35405B"
    });
}

public sealed class FullChargePlugin : IPowerHubPlugin
{
    private readonly Dictionary<string, (int Percent, bool? Charging)> previous = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> notified = new(StringComparer.OrdinalIgnoreCase);

    public PluginOutput OnScan(PluginContext context)
    {
        var notifications = new List<PluginNotification>();
        foreach (var device in context.Devices)
        {
            if (!device.ConnectionKnown || !device.IsConnected || device.BatteryPercent is not int percent || percent is < 0 or > 100) continue;
            if (percent < 100) notified.Remove(device.Id);
            if (percent == 100 && previous.TryGetValue(device.Id, out var last) &&
                (last.Percent < 100 || last.Charging == true && device.IsCharging == false) && notified.Add(device.Id))
                notifications.Add(new(L.T("设备已充满"), L.F("{0} · 100%，本轮充电已完成", device.Name)));
            previous[device.Id] = (percent, device.IsCharging);
        }
        return new(Notifications: notifications);
    }

    public void Dispose() { previous.Clear(); notified.Clear(); }
}

public sealed class QuietHoursPlugin : IPowerHubPlugin
{
    public PluginOutput OnScan(PluginContext context)
    {
        var startText = context.Options.TryGetValue("start", out var startValue) ? startValue : "22:00";
        var endText = context.Options.TryGetValue("end", out var endValue) ? endValue : "08:00";
        if (!TimeOnly.TryParseExact(startText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(endText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) return new();
        var current = TimeOnly.FromTimeSpan(context.Now.TimeOfDay);
        var quiet = start == end || (start < end ? current >= start && current < end : current >= start || current < end);
        return new(SuppressLowBatteryAlerts: quiet);
    }
}
