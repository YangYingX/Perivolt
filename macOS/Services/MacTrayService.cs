using Avalonia;
using Avalonia.Controls;
using PowerHub.Services;

namespace PowerHub.Mac.Services;

internal sealed class MacTrayService : IDisposable
{
    private readonly AppController controller;
    private readonly TrayIcon icon;
    private readonly WindowIcon image;
    private bool disposed;

    public MacTrayService(AppController controller)
    {
        this.controller = controller;
        using var stream = typeof(MacTrayService).Assembly.GetManifestResourceStream("PowerHub.TrayIcon.png")
            ?? throw new InvalidOperationException("The menu bar icon is missing.");
        image = new WindowIcon(stream);
        icon = new TrayIcon { Icon = image, IsVisible = true };
        icon.Clicked += OnClicked;
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { icon });
        Refresh();
    }

    private void OnClicked(object? sender, EventArgs args) => controller.ShowMain();

    public void Refresh()
    {
        if (disposed) return;
        var menu = new NativeMenu();
        var visible = controller.Devices.Where(device => device.ConnectionKnown && device.IsConnected && device.HasBattery).ToArray();
        foreach (var device in visible.Take(16))
            menu.Items.Add(new NativeMenuItem(controller.Settings.GetDeviceName(device) + " · " + device.BatteryText) { IsEnabled = false });
        if (visible.Length > 0) menu.Items.Add(new NativeMenuItemSeparator());
        Add(menu, L.T("打开主面板"), controller.ShowMain);
        Add(menu, L.T("刷新设备"), () => _ = controller.RefreshAsync());
        var notifications = new NativeMenuItem(L.T("开启充电提醒")) { IsChecked = controller.Settings.NotificationsEnabled, ToggleType = NativeMenuItemToggleType.CheckBox };
        notifications.Click += (_, _) => { controller.Settings.NotificationsEnabled = !controller.Settings.NotificationsEnabled; controller.ApplySettings(); };
        menu.Items.Add(notifications);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add(menu, L.T("退出"), controller.Exit);
        icon.Menu = menu;
        icon.ToolTipText = L.AppName + " · " + (visible.Length == 0 ? L.T("尚未发现设备") : string.Join(" / ", visible.Take(4).Select(device => controller.Settings.GetDeviceName(device) + " " + device.BatteryText)));
    }

    private static void Add(NativeMenu menu, string text, Action action)
    {
        var item = new NativeMenuItem(text);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.Clicked -= OnClicked;
        icon.IsVisible = false;
        icon.Dispose();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons());
    }
}
