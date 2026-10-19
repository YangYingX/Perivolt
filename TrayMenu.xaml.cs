using System.Windows;
using PowerHub.Models;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PowerHub;

public partial class TrayMenu : ContextMenu
{
    private readonly AppSettings settings;
    private readonly Action<string> command;
    private readonly Action settingsChanged;
    private IReadOnlyList<DeviceStatus> devices = [];

    public TrayMenu(AppSettings settings, Action<string> command, Action settingsChanged)
    {
        this.settings = settings;
        this.command = command;
        this.settingsChanged = settingsChanged;
        InitializeComponent();
        Refresh([]);
    }

    public void Refresh(IReadOnlyList<DeviceStatus> devices)
    {
        this.devices = devices;
        var online = devices.Where(device => device.ConnectionKnown && device.IsConnected).ToArray();
        var low = online.Count(device => device.IsCharging != true &&
            device.BatteryPercent is int percent && percent >= 0 && percent <= settings.GetThreshold(device.Id));
        StatusSummary.Text = devices.Count == 0 ? "暂无在线设备"
            : online.Length == 0 ? "当前没有在线设备"
            : low > 0 ? $"{online.Length} 个在线 · {low} 个需要充电"
            : $"{online.Length} 个在线 · {online.Count(device => device.HasBattery)} 个可读取电量";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            low > 0 ? "Warning" : online.Length > 0 ? "Accent" : "TextSecondary");
        NotificationsItem.IsChecked = settings.NotificationsEnabled;
        NotificationsDescription.Text = $"全局阈值 {settings.LowBatteryThreshold}% · {(settings.NotificationsEnabled ? "已开启" : "已关闭")}";
        AnimationsItem.IsChecked = settings.AnimationsEnabled;
        SystemThemeItem.IsChecked = settings.Theme == "System";
        LightThemeItem.IsChecked = settings.Theme == "Light";
        DarkThemeItem.IsChecked = settings.Theme == "Dark";
        OceanThemeItem.IsChecked = settings.Theme == "Ocean";
        LavenderThemeItem.IsChecked = settings.Theme == "Lavender";
        SunsetThemeItem.IsChecked = settings.Theme == "Sunset";
        ForestThemeItem.IsChecked = settings.Theme == "Forest";
        ThemeDescription.Text = settings.Theme switch
        {
            "Light" => "清透浅色", "Dark" => "静谧深色", "Ocean" => "深海蓝",
            "Lavender" => "薰衣草", "Sunset" => "暖杏", "Forest" => "森林绿", _ => "跟随系统"
        };
        RingStyleItem.IsChecked = settings.DisplayStyle == "Ring";
        BarStyleItem.IsChecked = settings.DisplayStyle == "Bar";
        HourglassStyleItem.IsChecked = settings.DisplayStyle == "Hourglass";
        OceanStyleItem.IsChecked = settings.DisplayStyle == "Ocean";
        DisplayDescription.Text = settings.DisplayStyle switch
        {
            "Ring" => "圆环", "Bar" => "电池条", "Hourglass" => "沙漏", _ => "海洋球"
        };
    }

    private void OnActionClick(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is MenuItem { Tag: string action }) command(action);
    }

    private void OnNotificationsClick(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        settings.NotificationsEnabled = NotificationsItem.IsChecked;
        CommitSettings();
    }

    private void OnAnimationsClick(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        settings.AnimationsEnabled = AnimationsItem.IsChecked;
        CommitSettings();
    }

    private void OnThemeClick(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is not MenuItem { Tag: string theme }) return;
        settings.Theme = theme;
        CommitSettings();
    }

    private void OnDisplayStyleClick(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is not MenuItem { Tag: string style }) return;
        settings.DisplayStyle = style;
        CommitSettings();
    }

    private void CommitSettings()
    {
        settingsChanged();
        Refresh(devices);
    }
}
