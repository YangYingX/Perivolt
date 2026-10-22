using PowerHub.Models;

namespace PowerHub.Services;

public sealed class DesktopWidgetManager(AppSettings settings, Action settingsChanged, Action openDashboard)
{
    private DesktopWidgetWindow? stack;
    private readonly Dictionary<string, DesktopWidgetWindow> cards = new(StringComparer.OrdinalIgnoreCase);
    private bool closed;

    public DesktopWidgetWindow? PreviewWindow => settings.WidgetLayout == "Free"
        ? cards.Values.FirstOrDefault(window => window.IsVisible) : stack;

    public void Apply(IReadOnlyList<DeviceStatus> devices) => Sync(devices, true);
    public void UpdateDevices(IReadOnlyList<DeviceStatus> devices) => Sync(devices, false);

    private void Sync(IReadOnlyList<DeviceStatus> devices, bool applySettings)
    {
        if (closed) return;
        if (!settings.WidgetEnabled)
        {
            stack?.Hide();
            foreach (var window in cards.Values) window.Hide();
            return;
        }
        if (settings.WidgetLayout != "Free")
        {
            foreach (var window in cards.Values) window.CloseForExit();
            cards.Clear();
            stack ??= new DesktopWidgetWindow(settings, settingsChanged, openDashboard);
            if (applySettings) stack.ApplySettings();
            stack.UpdateDevices(devices);
            if (!stack.IsVisible)
            {
                stack.RestorePosition();
                stack.Show();
            }
            return;
        }

        stack?.Hide();
        var pinned = devices.Where(device => device.ConnectionKnown && device.IsConnected && settings.IsPinned(device.Id))
            .OrderBy(device => settings.GetDeviceOrder(device.Id))
            .GroupBy(device => settings.ResolveDeviceId(device.Id), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        var online = pinned.Select(device => settings.ResolveDeviceId(device.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in cards.Keys.Where(id => !online.Contains(id)).ToArray())
        {
            cards[id].CloseForExit();
            cards.Remove(id);
        }
        for (var index = 0; index < pinned.Length; index++)
        {
            var id = settings.ResolveDeviceId(pinned[index].Id);
            if (!cards.TryGetValue(id, out var window))
            {
                window = new DesktopWidgetWindow(settings, settingsChanged, openDashboard, id,
                    () => cards.Values.ToArray(), index);
                cards.Add(id, window);
            }
            if (applySettings) window.ApplySettings();
            window.UpdateDevices(devices);
            if (!window.IsVisible)
            {
                window.RestorePosition();
                window.Show();
            }
        }
    }

    public void Recover()
    {
        if (closed) return;
        if (settings.WidgetLayout == "Free")
        {
            settings.WidgetPositions.Clear();
            var index = 0;
            foreach (var window in cards.Values) window.ResetPosition(index++);
        }
        else stack?.ResetPosition();
    }

    public void RestorePositions()
    {
        if (closed) return;
        if (settings.WidgetLayout == "Free")
        {
            foreach (var window in cards.Values.Where(window => window.IsVisible)) window.RestorePosition();
        }
        else if (stack is { IsVisible: true }) stack.RestorePosition();
    }

    public void FlushPositions()
    {
        if (closed) return;
        if (stack is { IsVisible: true }) stack.FlushPosition();
        foreach (var window in cards.Values.Where(window => window.IsVisible)) window.FlushPosition();
    }

    public void Close()
    {
        if (closed) return;
        closed = true;
        stack?.CloseForExit();
        foreach (var window in cards.Values) window.CloseForExit();
        cards.Clear();
    }
}
