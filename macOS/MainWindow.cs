using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using PowerHub.Mac.Controls;
using PowerHub.Mac.Services;
using PowerHub.Models;
using PowerHub.Services;

namespace PowerHub.Mac;

public sealed class MainWindow : Window
{
    private readonly AppController controller;
    private readonly List<Action> synchronize = [];
    private readonly Dictionary<string, Button> navigation = [];
    private MacThemePalette palette = MacThemePalette.Create("System");
    private ContentControl pageHost = new();
    private TextBlock pageTitle = new(), pageSubtitle = new(), status = new(), notice = new();
    private Button refreshButton = new();
    private StackPanel pinnedDevices = new();
    private WrapPanel overviewCards = new();
    private Grid overviewStats = new();
    private TextBox? search, diagnostics;
    private string page = "devices", language = "", theme = "", filter = "", pinnedSignature = "";
    private string? selectedDeviceId;
    private bool updating, closeForExit;

    public MainWindow(AppController controller)
    {
        this.controller = controller;
        Width = 1080;
        Height = 780;
        MinWidth = 960;
        MinHeight = 660;
        FontFamily = MacThemePalette.InterfaceFont;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        controller.Changed += OnControllerChanged;
        ActualThemeVariantChanged += (_, _) => { if (controller.Settings.Theme == "System") Refresh(); };
        Closing += (_, e) =>
        {
            if (closeForExit || controller.IsExiting) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) => controller.Changed -= OnControllerChanged;
        Refresh();
    }

    public void CloseForExit()
    {
        closeForExit = true;
        Close();
    }

    public void Navigate(string page)
    {
        this.page = page is "settings" or "diagnostics" ? page : "devices";
        BuildPage();
        Refresh();
    }

    private void OnControllerChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    public void Refresh()
    {
        if (closeForExit || updating) return;
        updating = true;
        try
        {
            var next = MacThemePalette.Create(controller.Settings.Theme);
            var nextTheme = controller.Settings.Theme + ":" + next.IsDark;
            if (language != controller.Settings.Language || theme != nextTheme)
            {
                language = controller.Settings.Language;
                theme = nextTheme;
                palette = next;
                BuildShell();
            }
            status.Text = controller.StatusText;
            refreshButton.Content = L.T(controller.IsScanning ? "正在更新…" : "↻  刷新电量");
            refreshButton.IsEnabled = !controller.IsScanning;
            foreach (var item in navigation) item.Value.Background = item.Key == page ? palette.AccentSoft : Brushes.Transparent;
            if (page == "devices") RefreshOverview();
            else if (page == "settings")
            {
                foreach (var sync in synchronize) sync();
                RefreshPinnedDevices();
            }
            else if (diagnostics is not null) diagnostics.Text = string.Join(Environment.NewLine, controller.Diagnostics);
        }
        finally { updating = false; }
    }

    private void BuildShell()
    {
        RequestedThemeVariant = controller.Settings.Theme == "System" ? ThemeVariant.Default : palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        Title = L.T("PowerHub · 外设电量中心") + " · macOS";
        Background = palette.Page;
        Foreground = palette.Text;
        FontSize = 13;
        navigation.Clear();
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("186,*") };
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16, 26, 16, 20) };
        var brand = new StackPanel { Spacing = 7, Margin = new Thickness(10, 0, 0, 32) };
        brand.Children.Add(Text(L.T("AppName"), 23, bold: true));
        brand.Children.Add(Text("macOS · 开发预览", 11, muted: true));
        sidebar.Children.Add(brand);
        var links = new StackPanel { Spacing = 7 };
        foreach (var item in new[] { ("devices", "设备概览"), ("settings", "设置"), ("diagnostics", "兼容性诊断") })
        {
            var button = ActionButton(L.T(item.Item2), () => Navigate(item.Item1));
            button.Height = 44;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            navigation[item.Item1] = button;
            links.Children.Add(button);
        }
        Grid.SetRow(links, 1);
        sidebar.Children.Add(links);
        var footer = new StackPanel { Spacing = 7, Margin = new Thickness(10, 0, 0, 0) };
        footer.Children.Add(Text("v" + UpdateService.DisplayVersion, 11, muted: true));
        footer.Children.Add(Text("关闭面板后继续在菜单栏运行。", 11, muted: true));
        Grid.SetRow(footer, 2);
        sidebar.Children.Add(footer);
        shell.Children.Add(new Border { Background = palette.Surface, BorderBrush = palette.Stroke, BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar });

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(28, 24, 28, 18) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        var heading = new StackPanel { Spacing = 6 };
        pageTitle = Text("", 24, bold: true);
        pageSubtitle = Text("", 12, muted: true);
        heading.Children.Add(pageTitle);
        heading.Children.Add(pageSubtitle);
        header.Children.Add(heading);
        refreshButton = ActionButton(L.T("↻  刷新电量"), async () => await controller.RefreshAsync());
        refreshButton.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(refreshButton, 1);
        header.Children.Add(refreshButton);
        main.Children.Add(header);
        pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(pageHost, 1);
        main.Children.Add(pageHost);
        var bottom = new StackPanel { Spacing = 5, Margin = new Thickness(0, 12, 0, 0) };
        notice = Text("", 11, muted: true);
        status = Text(controller.StatusText, 11, muted: true);
        bottom.Children.Add(notice);
        bottom.Children.Add(status);
        Grid.SetRow(bottom, 2);
        main.Children.Add(bottom);
        Grid.SetColumn(main, 1);
        shell.Children.Add(main);
        Content = shell;
        BuildPage();
    }

    private void BuildPage()
    {
        synchronize.Clear();
        diagnostics = null;
        pageTitle.Text = L.T(page switch { "settings" => "提醒与外观设置", "diagnostics" => "兼容性诊断", _ => "我的外设" });
        pageSubtitle.Text = page switch
        {
            "settings" => L.T("从默认配置开始，也能为每台设备保留自己的习惯。"),
            "diagnostics" => "macOS 开发预览 · 读取范围、运行信息与本地诊断",
            _ => controller.PreviewMode ? "界面预览 · 示例设备，不读取真实 macOS 外设" : L.T("仅显示当前已连接的外设；未提供电量的设备显示为未知。")
        };
        Control content = page switch { "settings" => BuildSettings(), "diagnostics" => BuildDiagnostics(), _ => BuildOverview() };
        pageHost.Content = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private Control BuildOverview()
    {
        var body = new StackPanel { Spacing = 18 };
        overviewStats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        body.Children.Add(overviewStats);
        search = new TextBox { Watermark = L.T("搜索名称、厂商或连接方式"), Text = filter, Margin = new Thickness(0, 0, 0, 2) };
        search.TextChanged += (_, _) => { filter = search.Text ?? ""; RefreshOverview(); };
        body.Children.Add(search);
        overviewCards = new WrapPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(overviewCards);
        RefreshOverview();
        return body;
    }

    private DeviceStatus[] OnlineDevices() => controller.Devices.Where(device => device.ConnectionKnown && device.IsConnected)
        .OrderBy(device => controller.Settings.GetDeviceOrder(device.Id)).ThenBy(device => controller.Settings.GetDeviceName(device), StringComparer.CurrentCultureIgnoreCase).ToArray();

    private void RefreshOverview()
    {
        var devices = OnlineDevices();
        overviewStats.Children.Clear();
        var values = new[]
        {
            (L.T("已连接外设"), devices.Length),
            (L.T("可读电量"), devices.Count(device => device.HasBattery)),
            (L.T("需要充电"), devices.Count(device => device.IsCharging != true && (device.BatteryPercent is int value ? value <= controller.Settings.GetThreshold(device.Id) : device.IsBatteryLow == true)))
        };
        for (var index = 0; index < values.Length; index++)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(Text(values[index].Item2.ToString(), 28, bold: true));
            stack.Children.Add(Text(values[index].Item1, 11, muted: true));
            var card = Card(stack, 18);
            Grid.SetColumn(card, index);
            overviewStats.Children.Add(card);
        }
        overviewCards.Children.Clear();
        var matching = devices.Where(device => string.IsNullOrWhiteSpace(filter) ||
            (controller.Settings.GetDeviceName(device) + " " + device.Manufacturer + " " + L.T(device.Connection)).Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
        if (matching.Length == 0)
        {
            var empty = new StackPanel { Spacing = 10, Margin = new Thickness(20, 42) };
            empty.Children.Add(Text(L.T(devices.Length == 0 ? "当前没有已连接的外设" : "没有符合条件的设备"), 17, bold: true));
            empty.Children.Add(Text(L.T("连接外设后刷新，或调整搜索与筛选条件。"), 12, muted: true));
            overviewCards.Children.Add(Card(empty));
            return;
        }
        foreach (var device in matching) overviewCards.Children.Add(DeviceCard(device));
    }

    private Border DeviceCard(DeviceStatus device)
    {
        var body = new StackPanel { Spacing = 12 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = Text(controller.Settings.GetDeviceName(device), 15, bold: true);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(name, name.Text);
        top.Children.Add(name);
        var pin = ActionButton(L.T(controller.Settings.IsPinned(device.Id) ? "✓ 已固定" : "+ 固定桌面"), () => controller.SetPinned(device.Id, !controller.Settings.IsPinned(device.Id)));
        pin.FontSize = 11;
        pin.Padding = new Thickness(8, 5);
        pin.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(pin, 1);
        top.Children.Add(pin);
        body.Children.Add(top);
        var details = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*"), ColumnSpacing = 12 };
        details.Children.Add(new BatteryVisual(controller.Settings, device.Id, device) { Width = 94, Height = 76 });
        var info = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Text(device.ChargingText, 12));
        info.Children.Add(Text(L.T(device.Category) + " · " + L.T(device.Connection), 11, muted: true));
        info.Children.Add(Text(L.F("读取于 {0}", device.UpdatedText), 10, muted: true));
        Grid.SetColumn(info, 1);
        details.Children.Add(info);
        body.Children.Add(details);
        var settingsButton = ActionButton(L.T("设置此设备的提醒与外观"), () => { selectedDeviceId = device.Id; Navigate("settings"); });
        settingsButton.FontSize = 11;
        settingsButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        body.Children.Add(settingsButton);
        return new Border { Width = 310, Padding = new Thickness(17), Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(15), Background = palette.Surface, BorderBrush = palette.Stroke, BorderThickness = new Thickness(1), Child = body };
    }

    private Control BuildSettings()
    {
        var body = new StackPanel { Spacing = 16 };
        var targetChoices = new List<Choice> { new("", L.T("默认配置")) };
        targetChoices.AddRange(controller.Settings.Devices.Select(pair => new Choice(pair.Key,
            string.IsNullOrWhiteSpace(pair.Value.CustomName) ? pair.Value.DisplayName : pair.Value.CustomName!)).Where(choice => !string.IsNullOrWhiteSpace(choice.Label)));
        foreach (var device in OnlineDevices())
            if (targetChoices.All(choice => choice.Id != device.Id)) targetChoices.Add(new(device.Id, controller.Settings.GetDeviceName(device)));
        if (selectedDeviceId is not null && targetChoices.All(choice => choice.Id != selectedDeviceId)) selectedDeviceId = null;
        var targetId = selectedDeviceId;
        DevicePreference TargetPreference() => controller.GetPreference(targetId!);
        var target = new ComboBox { ItemsSource = targetChoices, SelectedItem = targetChoices.First(choice => choice.Id == (selectedDeviceId ?? "")), HorizontalAlignment = HorizontalAlignment.Stretch };
        target.SelectionChanged += (_, _) =>
        {
            if (updating || target.SelectedItem is not Choice choice) return;
            selectedDeviceId = choice.Id.Length == 0 ? null : choice.Id;
            BuildPage();
            Refresh();
        };
        body.Children.Add(Card(Section(L.T("选择要设置的设备"), L.T("设置所选设备；默认配置用于未单独设置的设备"), target)));

        var reminders = Section(L.T("充电提醒"), L.T(targetId is null ? "设置所有设备的提醒总开关与默认值。" : "仅设置所选设备，断开连接后仍会记住。"));
        reminders.Children.Add(Toggle(L.T("启用提醒"), () => targetId is null ? controller.Settings.NotificationsEnabled : TargetPreference().NotificationsEnabled,
            value => { if (targetId is null) controller.Settings.NotificationsEnabled = value; else TargetPreference().NotificationsEnabled = value; }));
        reminders.Children.Add(Number(L.T("充电提醒阈值"), "%", 1, 100,
            () => targetId is null ? controller.Settings.LowBatteryThreshold : controller.Settings.GetThreshold(targetId),
            value => { if (targetId is null) controller.Settings.LowBatteryThreshold = value; else TargetPreference().LowBatteryThreshold = value; }));
        reminders.Children.Add(Number(L.T("重复提醒间隔"), "min", 5, 1440,
            () => controller.Settings.GetRepeatReminderMinutes(targetId),
            value => { if (targetId is null) controller.Settings.RepeatReminderMinutes = value; else TargetPreference().RepeatReminderMinutes = value; }));
        reminders.Children.Add(Text(L.T("持续低电时，按此间隔再次提醒。"), 11, muted: true));
        body.Children.Add(Card(reminders));

        var appearance = Section(L.T("设备外观"), L.T("设备概览与桌面卡片会同步使用所选样式。"));
        if (targetId is not null)
        {
            var customName = new TextBox { Text = TargetPreference().CustomName ?? "", Watermark = L.T("自定义设备名称"), MaxLength = 80 };
            void SaveName()
            {
                var value = string.IsNullOrWhiteSpace(customName.Text) ? null : customName.Text.Trim();
                if (TargetPreference().CustomName == value) return;
                TargetPreference().CustomName = value;
                Apply();
            }
            customName.LostFocus += (_, _) => SaveName();
            customName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { SaveName(); e.Handled = true; } };
            appearance.Children.Add(customName);
            appearance.Children.Add(Text(L.T("按 Enter 或离开输入框保存"), 11, muted: true));
        }
        appearance.Children.Add(Select(L.T("电量样式"), [new("Ring", L.T("圆环")), new("Bar", L.T("条形")), new("Hourglass", L.T("沙漏")), new("Ocean", L.T("海洋球")), new("Fill", L.T("整卡填充"))],
            () => controller.Settings.GetDisplayStyle(targetId), value => { if (targetId is null) controller.Settings.DisplayStyle = value; else TargetPreference().DisplayStyle = value; }));
        appearance.Children.Add(Toggle(L.T("电量动效"), () => controller.Settings.GetAnimationsEnabled(targetId),
            value => { if (targetId is null) controller.Settings.AnimationsEnabled = value; else TargetPreference().AnimationsEnabled = value; }));
        appearance.Children.Add(Toggle(L.T("显示百分比"), () => controller.Settings.GetShowBatteryPercentage(targetId),
            value => { if (targetId is null) controller.Settings.ShowBatteryPercentage = value; else TargetPreference().ShowBatteryPercentage = value; }));
        body.Children.Add(Card(appearance));

        var general = Section(L.T("通用"), L.T("设置会自动保存。"));
        general.Children.Add(Select(L.T("显示语言"), L.Languages.Select(choice => new Choice(choice.Id, choice.Label)).ToArray(),
            () => controller.Settings.Language, value => controller.Settings.Language = value));
        general.Children.Add(Select(L.T("外观主题"), [new("System", L.T("跟随系统")), new("Light", L.T("清透浅色")), new("Dark", L.T("静谧深色")), new("Ocean", L.T("深海蓝")), new("Lavender", L.T("薰衣草")), new("Sunset", L.T("暖杏日落")), new("Forest", L.T("森林绿"))],
            () => controller.Settings.Theme, value => controller.Settings.Theme = value));
        general.Children.Add(Number(L.T("电量扫描间隔"), "s", 30, 900, () => controller.Settings.PollIntervalSeconds, value => controller.Settings.PollIntervalSeconds = value));
        general.Children.Add(Toggle("启动时仅显示菜单栏", () => controller.Settings.StartInTray, value => controller.Settings.StartInTray = value));
        var login = Toggle("登录后启动 Perivolt", () => controller.Settings.StartWithWindows, value => controller.Settings.StartWithWindows = value);
        login.IsEnabled = !controller.PreviewMode;
        general.Children.Add(login);
        general.Children.Add(Text(controller.PreviewMode ? "界面预览中不修改登录启动。" : "安装 Perivolt.app 后可开启登录启动；通知需获得 macOS 许可。", 11, muted: true));
        body.Children.Add(Card(general));

        var desktop = Section(L.T("桌面卡片"), L.T("以下选项作用于整个桌面卡片容器。"));
        desktop.Children.Add(Toggle(L.T("启用桌面卡片"), () => controller.Settings.WidgetEnabled, value => controller.Settings.WidgetEnabled = value));
        desktop.Children.Add(Toggle(L.T("始终置顶"), () => controller.Settings.WidgetTopmost, value => controller.Settings.WidgetTopmost = value));
        desktop.Children.Add(Toggle(L.T("锁定位置"), () => controller.Settings.WidgetLocked, value => controller.Settings.WidgetLocked = value));
        desktop.Children.Add(Toggle(L.T("简洁模式"), () => controller.Settings.WidgetCompact, value => controller.Settings.WidgetCompact = value));
        desktop.Children.Add(Toggle(L.T("半透明背景"), () => controller.Settings.WidgetTransparent, value => controller.Settings.WidgetTransparent = value));
        desktop.Children.Add(ActionButton(L.T("找回卡片"), () =>
        {
            controller.Settings.WidgetEnabled = true;
            controller.Settings.WidgetLocked = false;
            controller.Settings.WidgetLeft = controller.Settings.WidgetTop = null;
            Apply();
        }));
        desktop.Children.Add(Text(L.T("在桌面显示哪些设备"), 13, bold: true));
        pinnedDevices = new StackPanel { Spacing = 6 };
        pinnedSignature = "";
        desktop.Children.Add(pinnedDevices);
        RefreshPinnedDevices();
        body.Children.Add(Card(desktop));

        var updates = Section(L.T("版本与更新"), "macOS 开发预览 · v" + UpdateService.DisplayVersion);
        updates.Children.Add(Toggle(L.T("自动检查更新"), () => controller.Settings.AutoCheckUpdates, value => controller.Settings.AutoCheckUpdates = value, !controller.PreviewMode));
        updates.Children.Add(Text(L.T("默认离线。开启后每 12 小时检查一次新版本。"), 11, muted: true));
        updates.Children.Add(Text("只下载匹配本机的 macOS 包；校验后由你解压并替换应用。", 11, muted: true));
        var updateStatus = Text(controller.Updates.Status, 12);
        synchronize.Add(() => updateStatus.Text = controller.Updates.Status);
        updates.Children.Add(updateStatus);
        var updateActions = new WrapPanel { Orientation = Orientation.Horizontal };
        var checkUpdate = ActionButton(L.T("立即检查"), async () =>
        {
            if (!controller.Settings.AutoCheckUpdates) { SetNotice("请先开启自动检查更新，再检查发行版。"); return; }
            await controller.Updates.CheckAsync();
        });
        var download = ActionButton("下载 macOS 安装包", async () => await controller.Updates.DownloadAsync());
        var cancel = ActionButton(L.T("取消"), controller.Updates.CancelDownload);
        var release = ActionButton(L.T("查看新版本"), () => Open("https://github.com/YangYingX/Perivolt/releases/latest"));
        foreach (var button in new[] { checkUpdate, download, cancel, release }) { button.Margin = new Thickness(0, 0, 8, 8); updateActions.Children.Add(button); }
        synchronize.Add(() =>
        {
            checkUpdate.IsEnabled = !controller.PreviewMode && !controller.Updates.IsBusy;
            download.IsEnabled = !controller.PreviewMode && controller.Updates.CanDownloadInstaller && !controller.Updates.IsBusy;
            cancel.IsEnabled = !controller.PreviewMode && controller.Updates.IsDownloading;
            release.IsEnabled = !controller.PreviewMode;
        });
        updates.Children.Add(updateActions);
        body.Children.Add(Card(updates));
        return body;
    }

    private void RefreshPinnedDevices()
    {
        var devices = OnlineDevices();
        var signature = string.Join("|", devices.Select(device => device.Id + ":" + controller.Settings.GetDeviceName(device) + ":" + controller.Settings.IsPinned(device.Id)));
        if (pinnedSignature == signature && pinnedDevices.Children.Count > 0) return;
        pinnedSignature = signature;
        pinnedDevices.Children.Clear();
        if (devices.Length == 0) pinnedDevices.Children.Add(Text(L.T("当前没有在线设备，连接后可在这里选择。"), 12, muted: true));
        foreach (var device in devices)
        {
            var check = new CheckBox { Content = controller.Settings.GetDeviceName(device), IsChecked = controller.Settings.IsPinned(device.Id), Foreground = palette.Text };
            check.IsCheckedChanged += (_, _) => { if (!updating) controller.SetPinned(device.Id, check.IsChecked == true); };
            pinnedDevices.Children.Add(check);
        }
    }

    private Control BuildDiagnostics()
    {
        var body = new StackPanel { Spacing = 16 };
        var scope = Section("macOS 开发预览", "目前提供设备概览、充电提醒、外观设置和集中式桌面卡片。");
        scope.Children.Add(Text("电量来自 macOS 可读的系统属性，可能为系统缓存；型号与连接方式会影响读取覆盖。Windows 自动化、厂商协议和插件尚未移植。", 12, muted: true));
        body.Children.Add(Card(scope));
        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        var export = ActionButton(L.T("导出兼容性诊断"), ExportDiagnostics);
        var data = ActionButton("打开数据目录", controller.OpenDataDirectory);
        data.IsEnabled = !controller.PreviewMode;
        tools.Children.Add(export);
        data.Margin = new Thickness(10, 0, 0, 0);
        tools.Children.Add(data);
        body.Children.Add(tools);
        body.Children.Add(Text("诊断文件可能包含设备名称、硬件标识及本机路径，分享前请检查并删除个人信息。", 11, muted: true));
        diagnostics = new TextBox
        {
            Text = string.Join(Environment.NewLine, controller.Diagnostics), IsReadOnly = true,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 190, MaxHeight = 320,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 11,
            Background = palette.Surface, Foreground = palette.Text
        };
        body.Children.Add(diagnostics);
        var about = Section("Perivolt · 关于", "开发者：阳影X（YangYingX）");
        about.Children.Add(Text("个人非商业使用免费；企业使用、收费分发和商业改造须获得作者书面授权。历史 MIT 与第三方材料保留原有许可。", 12, muted: true));
        about.Children.Add(Text("默认离线，无需账号。更新检查由你开启；诊断在本机导出，不自动上传。", 12, muted: true));
        about.Children.Add(Text(controller.DataDirectory, 11, muted: true));
        var source = ActionButton("GitHub · YangYingX/Perivolt", () => Open("https://github.com/YangYingX/Perivolt"));
        source.IsEnabled = !controller.PreviewMode;
        about.Children.Add(source);
        body.Children.Add(Card(about));
        return body;
    }

    private async void ExportDiagnostics()
    {
        try
        {
            if (!StorageProvider.CanSave) { SetNotice("当前环境不支持保存对话框。"); return; }
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("导出兼容性诊断"), SuggestedFileName = "Perivolt-macOS-diagnostics.json", DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            await controller.ExportAsync(path);
            SetNotice(L.T("诊断文件已导出。"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { SetNotice(L.F("导出未完成：{0}", error.Message)); }
    }

    private void Open(string url)
    {
        if (controller.PreviewMode) return;
        try { MacPlatform.Open(url); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { SetNotice(error.Message); }
    }

    private void SetNotice(string value) => notice.Text = value;

    private void Apply()
    {
        if (updating) return;
        controller.ApplySettings();
        Refresh();
    }

    private CheckBox Toggle(string label, Func<bool> get, Action<bool> set, bool enabled = true)
    {
        var check = new CheckBox { Content = label, IsChecked = get(), Foreground = palette.Text, IsEnabled = enabled };
        check.IsCheckedChanged += (_, _) => { if (updating) return; set(check.IsChecked == true); Apply(); };
        synchronize.Add(() => check.IsChecked = get());
        return check;
    }

    private Control Number(string label, string unit, int min, int max, Func<int> get, Action<int> set)
    {
        var input = new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, Value = get(), FormatString = "0", Width = 116 };
        input.ValueChanged += (_, _) => { if (updating || input.Value is not decimal value) return; set((int)value); Apply(); };
        synchronize.Add(() => { if (!input.IsKeyboardFocusWithin) input.Value = get(); });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,38"), ColumnSpacing = 10 };
        row.Children.Add(Text(label, 13));
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        var suffix = Text(unit, 11, muted: true);
        suffix.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(suffix, 2);
        row.Children.Add(suffix);
        return row;
    }

    private Control Select(string label, Choice[] choices, Func<string> get, Action<string> set)
    {
        var combo = new ComboBox { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(choice => choice.Id == get()) ?? choices[0], MinWidth = 210, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectionChanged += (_, _) => { if (updating || combo.SelectedItem is not Choice choice) return; set(choice.Id); Apply(); };
        synchronize.Add(() => { if (!combo.IsDropDownOpen) combo.SelectedItem = choices.FirstOrDefault(choice => choice.Id == get()) ?? choices[0]; });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 18 };
        var text = Text(label, 13);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        return row;
    }

    private StackPanel Section(string title, string description, params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Text(title, 16, bold: true));
        if (description.Length > 0) panel.Children.Add(Text(description, 11, muted: true));
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    private Border Card(Control child, double padding = 22) => new()
    {
        Background = palette.Surface, BorderBrush = palette.Stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(15), Padding = new Thickness(padding), Child = child
    };

    private TextBlock Text(string value, double size = 13, bool bold = false, bool muted = false) => new()
    {
        Text = value, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        Foreground = muted ? palette.Muted : palette.Text, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    private Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 8), Foreground = palette.Text };
        button.Click += (_, _) => action();
        return button;
    }

    private sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}

internal sealed record MacThemePalette(IBrush Page, IBrush Surface, IBrush SurfaceAlt, IBrush Stroke, IBrush Text, IBrush Muted, IBrush Accent, IBrush AccentSoft, bool IsDark)
{
    public static FontFamily InterfaceFont { get; } = new("PingFang SC, Microsoft YaHei UI, Microsoft YaHei, Segoe UI, sans-serif");

    public static MacThemePalette Create(string theme)
    {
        var dark = theme is "Dark" or "Ocean" or "Forest" || theme == "System" && Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var values = theme switch
        {
            "Ocean" => new[] { "#0C1727", "#13233A", "#1C304A", "#2B4562", "#EAF4FF", "#9FB9D1", "#78C9FF", "#203E5B" },
            "Lavender" => new[] { "#F3F0FA", "#FCFAFF", "#EEE8F6", "#E0D6ED", "#352C49", "#81728F", "#8560B3", "#EDE2F8" },
            "Sunset" => new[] { "#FBF2EC", "#FFFCF9", "#F6E8DE", "#EEDCCF", "#493429", "#927866", "#B76B49", "#F8E2D2" },
            "Forest" => new[] { "#101D1A", "#192C25", "#243B31", "#355044", "#EFF8ED", "#A5C0AC", "#B8DC91", "#314A2D" },
            _ => dark
                ? new[] { "#0E121B", "#171D29", "#202838", "#2B3546", "#F0F4FA", "#A2AEC1", "#8FE4C5", "#203B34" }
                : new[] { "#F3F5F9", "#FFFFFF", "#EDF1F6", "#E1E6EF", "#18253B", "#697990", "#14866B", "#E0F3EB" }
        };
        var brushes = values.Select(value => (IBrush)new SolidColorBrush(Color.Parse(value))).ToArray();
        return new(brushes[0], brushes[1], brushes[2], brushes[3], brushes[4], brushes[5], brushes[6], brushes[7], dark);
    }
}
