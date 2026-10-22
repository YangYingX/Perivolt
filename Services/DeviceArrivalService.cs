using PowerHub.Models;

namespace PowerHub.Services;

public sealed class DeviceArrivalService(AppSettings settings, Action<string> openDevice) : IDisposable
{
    private readonly DeviceArrivalPolicy policy = new();
    private readonly List<DeviceStatus> pending = [];
    private DeviceArrivalWindow? window;
    private bool disposed;
    private bool Enabled => !disposed && settings.DeviceArrivalEnabled && settings.NotificationsEnabled;

    public void Observe(ScanResult result)
    {
        if (disposed) return;
        var online = result.Devices.Where(device => device.ConnectionKnown && device.IsConnected)
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        pending.RemoveAll(device => !online.ContainsKey(device.Id));
        for (var index = 0; index < pending.Count; index++) pending[index] = online[pending[index].Id];
        foreach (var arrival in policy.Observe(result, DateTimeOffset.UtcNow, Enabled))
        {
            if (pending.Count + (window is null ? 0 : 1) >= 3) break;
            pending.Add(arrival);
        }
        if (window is not null)
        {
            if (online.TryGetValue(window.DeviceId, out var current)) window.UpdateDevice(current);
            else window.Close();
        }
        ShowNext();
    }

    public void ApplySettings()
    {
        if (disposed) return;
        if (!Enabled) { Clear(); return; }
        window?.ApplySettings();
    }

    public void ResetBaseline()
    {
        if (disposed) return;
        policy.Reset();
        Clear();
    }

    private void ShowNext()
    {
        if (!Enabled || window is not null || pending.Count == 0) return;
        var device = pending[0];
        pending.RemoveAt(0);
        var next = new DeviceArrivalWindow(settings, device, id =>
        {
            pending.Clear();
            window?.Close();
            if (!disposed) openDevice(id);
        });
        window = next;
        next.Closed += (_, _) =>
        {
            if (!ReferenceEquals(window, next)) return;
            window = null;
            ShowNext();
        };
        next.Show();
    }

    private void Clear()
    {
        pending.Clear();
        window?.Close();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Clear();
        policy.Reset();
    }
}
