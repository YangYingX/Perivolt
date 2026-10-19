using PowerHub.Models;

namespace PowerHub.Services;

public sealed class ReminderPolicy
{
    private readonly Dictionary<string, DateTimeOffset> lastNotified = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceStatus> Evaluate(ScanResult result, DateTimeOffset scanStartedAt,
        AppSettings settings, DateTimeOffset now)
    {
        var reminders = new List<DeviceStatus>();
        foreach (var device in result.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id) || !device.ConnectionKnown || !device.IsConnected
                || device.UpdatedAt is not { } updatedAt || updatedAt < scanStartedAt || updatedAt > now)
                continue;

            var threshold = settings.GetThreshold(device.Id);
            if (device.IsCharging == true || device.BatteryPercent is int recovered && recovered > threshold && recovered <= 100
                || device.BatteryPercent is null && device.IsBatteryLow == false)
            {
                lastNotified.Remove(device.Id);
                continue;
            }

            var low = device.BatteryPercent is int percent ? percent >= 0 && percent <= threshold : device.IsBatteryLow == true;
            if (!settings.IsDeviceNotificationEnabled(device.Id) || !low)
                continue;

            if (lastNotified.TryGetValue(device.Id, out var notifiedAt)
                && now - notifiedAt < TimeSpan.FromMinutes(settings.GetRepeatReminderMinutes(device.Id)))
                continue;

            lastNotified[device.Id] = now;
            reminders.Add(device);
        }
        return reminders;
    }
}
