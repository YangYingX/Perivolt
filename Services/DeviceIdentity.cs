using PowerHub.Models;

namespace PowerHub.Services;

public static class DeviceIdentity
{
    // Only explicit links are applied here. The scanner separately merges shared hardware identities.
    public static ScanResult ApplyLinks(ScanResult scan, AppSettings settings) => scan with
    {
        Devices = scan.Devices.GroupBy(device => settings.ResolveDeviceId(device.Id), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var readings = group.OrderByDescending(device => device.HasBattery).ThenByDescending(device => device.UpdatedAt).ToArray();
                var primary = readings[0];
                var name = settings.Devices.TryGetValue(group.Key, out var remembered) && !string.IsNullOrWhiteSpace(remembered.DisplayName)
                    ? remembered.DisplayName : primary.Name;
                return primary with { Id = group.Key, Name = name };
            }).ToArray()
    };
}
