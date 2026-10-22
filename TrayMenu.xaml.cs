using PowerHub.Services;
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
        StatusSummary.Text = devices.Count == 0 ? L.T("暂无在线设备")
            : online.Length == 0 ? L.T("当前没有在线设备")
            : low > 0 ? L.F("{0} 个在线 · {1} 个需要充电", online.Length, low)
            : L.F("{0} 个在线 · {1} 个可读取电量", online.Length, online.Count(device => device.HasBattery));
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            low > 0 ? "Warning" : online.Length > 0 ? "Accent" : "TextSecondary");
        NotificationsItem.IsChecked = settings.NotificationsEnabled;
        NotificationsDescription.Text = L.F("全局阈值 {0}% · {1}", settings.LowBatteryThreshold, (settings.NotificationsEnabled ? L.T("已开启") : L.T("已关闭")));
        AnimationsItem.IsChecked = settings.AnimationsEnabled;
        ScenesItem.Items.Clear();
        foreach (var scene in settings.DesktopScenes)
        {
            var item = new MenuItem { Header = new System.Windows.Controls.TextBlock { Text = scene.Name }, Tag = "scene:" + scene.Id };
            item.Click += OnActionClick;
            ScenesItem.Items.Add(item);
        }
        if (settings.DesktopScenes.Count > 0) ScenesItem.Items.Add(new System.Windows.Controls.Separator());
        var manageScenes = new MenuItem { Header = L.T("管理桌面场景"), Tag = "widget-settings" };
        manageScenes.Click += OnActionClick;
        ScenesItem.Items.Add(manageScenes);
        SystemThemeItem.IsChecked = settings.Theme == "System";
        LightThemeItem.IsChecked = settings.Theme == "Light";
        DarkThemeItem.IsChecked = settings.Theme == "Dark";
        OceanThemeItem.IsChecked = settings.Theme == "Ocean";
        LavenderThemeItem.IsChecked = settings.Theme == "Lavender";
        SunsetThemeItem.IsChecked = settings.Theme == "Sunset";
        ForestThemeItem.IsChecked = settings.Theme == "Forest";
        ThemeDescription.Text = settings.Theme switch
        {
            "Light" => L.T("清透浅色"), "Dark" => L.T("静谧深色"), "Ocean" => L.T("深海蓝"),
            "Lavender" => L.T("薰衣草"), "Sunset" => L.T("暖杏"), "Forest" => L.T("森林绿"), _ => L.T("跟随系统")
        };
        RingStyleItem.IsChecked = settings.DisplayStyle == "Ring";
        BarStyleItem.IsChecked = settings.DisplayStyle == "Bar";
        HourglassStyleItem.IsChecked = settings.DisplayStyle == "Hourglass";
        OceanStyleItem.IsChecked = settings.DisplayStyle == "Ocean";
        FillStyleItem.IsChecked = settings.DisplayStyle == "Fill";
        DisplayDescription.Text = settings.DisplayStyle switch
        {
            "Ring" => L.T("圆环"), "Bar" => L.T("电池条"), "Hourglass" => L.T("沙漏"), "Fill" => L.T("底色填充"), _ => L.T("海洋球")
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
