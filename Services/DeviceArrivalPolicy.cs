using PowerHub.Models;

namespace PowerHub.Services;

public sealed class DeviceArrivalPolicy
{
    private readonly Dictionary<string, bool?> connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lastNotified = new(StringComparer.OrdinalIgnoreCase);
    private bool initialized;

    public IReadOnlyList<DeviceStatus> Observe(ScanResult result, DateTimeOffset now, bool notificationsEnabled = true)
    {
        var snapshot = result.Devices.Where(device => !string.IsNullOrWhiteSpace(device.Id))
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(device => device.ConnectionKnown && device.IsConnected) ?? group.First()).ToArray();
        var present = snapshot.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in connections.Keys.Where(id => !present.Contains(id)).ToArray()) connections[id] = false;

        var arrivals = new List<DeviceStatus>();
        foreach (var device in snapshot)
        {
            var seen = connections.TryGetValue(device.Id, out var previous);
            if (!device.ConnectionKnown)
            {
                // Uncertain readings must not turn a later confirmed reading into a reconnect.
                if (!seen) connections[device.Id] = null;
                continue;
            }
            connections[device.Id] = device.IsConnected;
            if (!initialized || !notificationsEnabled || !device.IsConnected || seen && previous != false) continue;
            if (lastNotified.TryGetValue(device.Id, out var last) && now - last < TimeSpan.FromMinutes(1)) continue;
            if (arrivals.Count == 3) continue;
            lastNotified[device.Id] = now;
            arrivals.Add(device);
        }
        initialized = true;
        return arrivals;
    }

    public void Reset()
    {
        initialized = false;
        connections.Clear();
    }
}
