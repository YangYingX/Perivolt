using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PowerHub.Models;

namespace Perivolt.Extensions.PeripheralHub;

internal sealed class HubWindow : Window
{
    private readonly PeripheralHubPlugin _plugin;
    private readonly StackPanel _deviceRows = new();
    private readonly TextBlock _deviceCount = Caption("");
    private readonly TextBlock _scanTime = Caption("");
    private readonly ComboBox _outputs = new() { DisplayMemberPath = nameof(AudioEndpoint.Name), MinHeight = 36 };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, SmallChange = 1, LargeChange = 5, IsMoveToPointEnabled = true };
    private readonly TextBlock _volumeText = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, MinWidth = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _outputMute = ActionButton("静音输出");
    private readonly Button _microphoneMute = ActionButton("静音麦克风");
    private readonly TextBlock _microphoneName = Caption("等待系统音频设备…");
    private readonly TextBlock _microphoneState = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _outputError = Caption("");
    private readonly TextBlock _microphoneError = Caption("");
    private readonly TextBlock _hotkey = Caption("");
    private readonly TextBlock _status = Caption("");
    private AudioSnapshot? _audio;
    private AudioEndpoint[] _outputChoices = [];
    private bool _refreshing;
    private bool _draggingVolume;

    internal bool IsEditingAudio => _outputs.IsDropDownOpen || _draggingVolume;

    internal event Action<double>? VolumeChanged;
    internal event Action<bool>? OutputMuteRequested;
    internal event Action<bool>? MicrophoneMuteRequested;
    internal event Action<string>? OutputSelected;
    internal event Action? SoundSettingsRequested;
    internal event Action? AudioInteractionEnded;

    internal HubWindow(PeripheralHubPlugin plugin)
    {
        _plugin = plugin;
        Title = "外设快捷中枢 · 电栖";
        Width = 460;
        Height = 700;
        MinWidth = 380;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "PageBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape && !_outputs.IsDropDownOpen) { Close(); args.Handled = true; } };
        Loaded += (_, _) => FitWorkArea();

        var layout = new Grid { Margin = new(20, 18, 20, 16) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new(0, 0, 0, 17) };
        heading.Children.Add(new TextBlock { Text = "外设快捷中枢", FontSize = 23, FontWeight = FontWeights.SemiBold });
        var subtitle = Caption("电量、声音与麦克风，一处就绪。");
        subtitle.Margin = new(0, 6, 0, 0);
        heading.Children.Add(subtitle);
        layout.Children.Add(heading);

        var body = new StackPanel();
        var devicesHeader = new DockPanel { Margin = new(0, 0, 0, 8) };
        DockPanel.SetDock(_deviceCount, Dock.Right);
        _deviceCount.VerticalAlignment = VerticalAlignment.Center;
        devicesHeader.Children.Add(_deviceCount);
        devicesHeader.Children.Add(new TextBlock { Text = "外设电量", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        body.Children.Add(devicesHeader);
        var devicesScroll = new ScrollViewer
        {
            Content = _deviceRows, MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new(0, 0, 3, 0)
        };
        AutomationProperties.SetName(devicesScroll, "外设电量列表");
        body.Children.Add(devicesScroll);
        _scanTime.Margin = new(0, 6, 0, 0);
        body.Children.Add(_scanTime);
        body.Children.Add(Divider());

        var outputHeader = new Grid { Margin = new(0, 0, 0, 10) };
        outputHeader.ColumnDefinitions.Add(new());
        outputHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        outputHeader.Children.Add(new TextBlock { Text = "声音输出", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_outputMute, 1);
        outputHeader.Children.Add(_outputMute);
        body.Children.Add(outputHeader);
        AutomationProperties.SetName(_outputs, "Windows 默认声音输出设备");
        _outputs.ToolTip = "切换 Windows 默认输出；应用固定的输出不受影响。";
        body.Children.Add(_outputs);

        var volumeRow = new Grid { Margin = new(0, 11, 0, 0) };
        volumeRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        volumeRow.ColumnDefinitions.Add(new());
        volumeRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var volumeLabel = Caption("音量");
        volumeLabel.VerticalAlignment = VerticalAlignment.Center;
        volumeRow.Children.Add(volumeLabel);
        _volume.Margin = new(12, 0, 12, 0);
        _volume.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_volume, "系统输出音量百分比");
        Grid.SetColumn(_volume, 1);
        volumeRow.Children.Add(_volume);
        Grid.SetColumn(_volumeText, 2);
        volumeRow.Children.Add(_volumeText);
        body.Children.Add(volumeRow);
        ConfigureError(_outputError);
        body.Children.Add(_outputError);
        body.Children.Add(Divider());

        var microphoneHeader = new Grid { Margin = new(0, 0, 0, 8) };
        microphoneHeader.ColumnDefinitions.Add(new());
        microphoneHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        microphoneHeader.Children.Add(new TextBlock { Text = "默认通信麦克风", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_microphoneMute, 1);
        microphoneHeader.Children.Add(_microphoneMute);
        body.Children.Add(microphoneHeader);
        _microphoneName.TextTrimming = TextTrimming.CharacterEllipsis;
        _microphoneName.TextWrapping = TextWrapping.NoWrap;
        body.Children.Add(_microphoneName);
        _microphoneState.Margin = new(0, 6, 0, 0);
        body.Children.Add(_microphoneState);
        ConfigureError(_microphoneError);
        body.Children.Add(_microphoneError);

        var bodyScroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new(0, 0, 3, 0)
        };
        Grid.SetRow(bodyScroll, 1);
        layout.Children.Add(bodyScroll);

        var footer = new StackPanel { Margin = new(0, 13, 0, 0) };
        footer.Children.Add(new Border { Height = 1, Margin = new(0, 0, 0, 11) }.Resource(Border.BackgroundProperty, "Stroke"));
        _hotkey.FontSize = 11;
        footer.Children.Add(_hotkey);
        _status.FontSize = 11;
        _status.MaxHeight = 46;
        _status.Margin = new(0, 5, 0, 8);
        footer.Children.Add(_status);
        var footerRow = new DockPanel();
        var settings = ActionButton("Windows 声音设置");
        settings.Padding = new(10, 5, 10, 5);
        settings.FontSize = 11;
        settings.Click += (_, _) => SoundSettingsRequested?.Invoke();
        AutomationProperties.SetName(settings, "打开 Windows 声音设置");
        DockPanel.SetDock(settings, Dock.Right);
        footerRow.Children.Add(settings);
        var credit = Caption("电栖 · 阳影X");
        credit.FontSize = 11;
        credit.VerticalAlignment = VerticalAlignment.Center;
        footerRow.Children.Add(credit);
        footer.Children.Add(footerRow);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        Content = layout;

        _outputs.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _outputs.SelectedItem is AudioEndpoint endpoint && endpoint.Id != _audio?.DefaultOutputId)
                OutputSelected?.Invoke(endpoint.Id);
        };
        _outputs.DropDownClosed += (_, _) => AudioInteractionEnded?.Invoke();
        _volume.ValueChanged += (_, _) =>
        {
            if (_refreshing) return;
            _volumeText.Text = $"{(int)Math.Round(_volume.Value)}%";
            VolumeChanged?.Invoke(_volume.Value / 100d);
        };
        _volume.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _draggingVolume = true));
        _volume.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _draggingVolume = false;
            AudioInteractionEnded?.Invoke();
        }));
        _outputMute.Click += (_, _) => { if (_audio?.OutputMuted is bool muted) OutputMuteRequested?.Invoke(!muted); };
        _microphoneMute.Click += (_, _) => { if (_audio?.MicrophoneMuted is bool muted) MicrophoneMuteRequested?.Invoke(!muted); };
        SetHotkeyStatus(plugin.HotkeyStatus);
        SetStatus(plugin.Status);
        RefreshDevices();
        SetAudioWaiting();
    }

    internal void RefreshDevices()
    {
        _deviceRows.Children.Clear();
        var devices = _plugin.Devices;
        _deviceCount.Text = $"{devices.Count} 台设备";
        var now = DateTimeOffset.UtcNow;
        if (devices.Count == 0)
        {
            var empty = Caption("等待主程序发现外设。未提供电量的设备会保留为未知。");
            empty.Margin = new(0, 6, 0, 8);
            _deviceRows.Children.Add(empty);
        }
        foreach (var device in devices)
        {
            var row = new Grid { Margin = new(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new() { Width = new(36) });
            row.ColumnDefinitions.Add(new());
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var icon = new TextBlock
            {
                Text = DeviceIcon(device.Category), FontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 19, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            row.Children.Add(icon);
            var text = new StackPanel { Margin = new(0, 5, 10, 5) };
            var name = new TextBlock { Text = string.IsNullOrWhiteSpace(device.Name) ? device.Category : device.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.ToolTip = name.Text;
            text.Children.Add(name);
            var detail = Caption(DeviceDetail(device, now));
            detail.FontSize = 10.5;
            detail.Margin = new(0, 3, 0, 0);
            detail.TextWrapping = TextWrapping.NoWrap;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            detail.ToolTip = detail.Text + (string.IsNullOrWhiteSpace(device.Message) ? "" : "\n" + device.Message);
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var battery = new TextBlock { Text = BatteryText(device), FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 43, TextAlignment = TextAlignment.Right };
            var unavailable = device.ConnectionKnown && !device.IsConnected || device.BatteryPercent is not (>= 0 and <= 100) && device.BatteryLevel is null;
            battery.SetResourceReference(TextBlock.ForegroundProperty, unavailable ? "TextSecondary" : device.IsBatteryLow == true || device.BatteryPercent is >= 0 and <= 20 ? "Warning" : "Accent");
            Grid.SetColumn(battery, 2);
            row.Children.Add(battery);
            AutomationProperties.SetName(row, $"{name.Text}，{battery.Text}，{detail.Text}");
            _deviceRows.Children.Add(row);
        }
        _scanTime.Text = _plugin.LastScanAt == default ? "等待首次扫描" : $"主程序扫描 {_plugin.LastScanAt.ToLocalTime():HH:mm:ss} · 各设备采样时间见上方";
    }

    internal void RefreshAudio(AudioSnapshot snapshot)
    {
        _refreshing = true;
        try
        {
            _audio = snapshot;
            if (!_outputChoices.SequenceEqual(snapshot.Outputs))
            {
                _outputChoices = snapshot.Outputs.ToArray();
                _outputs.ItemsSource = _outputChoices;
            }
            if (!_outputs.IsDropDownOpen)
                _outputs.SelectedItem = _outputChoices.FirstOrDefault(endpoint => endpoint.Id == snapshot.DefaultOutputId);
            _outputs.IsEnabled = _outputChoices.Length > 0;
            _outputs.ToolTip = snapshot.DefaultOutputId is null ? "系统没有可确认的默认声音输出，可选择设备或打开 Windows 声音设置。" : "切换 Windows 默认输出；应用固定的输出不受影响。";
            _volume.IsEnabled = snapshot.Volume.HasValue;
            if (!_draggingVolume)
            {
                _volume.Value = snapshot.Volume is double volume ? Math.Clamp(volume * 100, 0, 100) : 0;
                _volumeText.Text = snapshot.Volume is double value ? $"{(int)Math.Round(Math.Clamp(value * 100, 0, 100))}%" : "—";
            }
            _outputMute.IsEnabled = snapshot.OutputMuted.HasValue;
            _outputMute.Content = snapshot.OutputMuted == true ? "取消输出静音" : "静音输出";
            AutomationProperties.SetName(_outputMute, snapshot.OutputMuted == true ? "输出已静音，点击取消静音" : snapshot.OutputMuted == false ? "静音默认声音输出" : "声音输出静音状态未知");
            SetError(_outputError, snapshot.OutputError);
            _microphoneName.Text = snapshot.MicrophoneName ?? "未找到默认通信麦克风";
            _microphoneName.ToolTip = _microphoneName.Text;
            _microphoneState.Text = snapshot.MicrophoneMuted switch { true => "已静音", false => "未静音", _ => "静音状态未知" };
            _microphoneState.SetResourceReference(TextBlock.ForegroundProperty, snapshot.MicrophoneMuted == true ? "Warning" : snapshot.MicrophoneMuted == false ? "Accent" : "TextSecondary");
            _microphoneMute.IsEnabled = snapshot.MicrophoneMuted.HasValue;
            _microphoneMute.Content = snapshot.MicrophoneMuted == true ? "取消麦克风静音" : "静音麦克风";
            AutomationProperties.SetName(_microphoneMute, snapshot.MicrophoneMuted == true ? "麦克风已静音，点击取消静音" : snapshot.MicrophoneMuted == false ? "静音默认麦克风" : "麦克风静音状态未知");
            SetError(_microphoneError, snapshot.MicrophoneError);
        }
        finally { _refreshing = false; }
    }

    internal void SetHotkeyStatus(string status) { _hotkey.Text = status; _hotkey.ToolTip = status; }
    internal void SetStatus(string status) { _status.Text = status; _status.ToolTip = status; }

    internal static string BatteryText(DeviceStatus device) => device.ConnectionKnown && !device.IsConnected ? "—" :
        device.BatteryPercent is >= 0 and <= 100 ? $"{device.BatteryPercent}%" : device.BatteryLevel is not null ? device.BatteryText : "—";

    internal static string DeviceDetail(DeviceStatus device, DateTimeOffset now)
    {
        if (device.ConnectionKnown && !device.IsConnected)
            return device.BatteryPercent is >= 0 and <= 100 ? $"未连接 · 上次读数 {device.BatteryPercent}%" : "未连接 · 电量未知";
        var state = !device.ConnectionKnown ? "连接未知" : "已连接";
        state += device.IsCharging switch { true => " · 正在充电", false => "", _ => " · 充电状态未知" };
        if (device.BatteryPercent is not (>= 0 and <= 100) && device.BatteryLevel is null) return state + " · 电量未知";
        var cached = device.Source.Contains("缓存", StringComparison.Ordinal) || device.Source.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
                     !device.Source.Contains("Windows", StringComparison.OrdinalIgnoreCase) && (device.Message.Contains("缓存", StringComparison.Ordinal) || device.Message.Contains("cache", StringComparison.OrdinalIgnoreCase));
        if (cached) state += " · 旧读数";
        else if (device.Source.Contains("Windows", StringComparison.OrdinalIgnoreCase)) state += " · 系统报告";
        if (device.UpdatedAt is not { } at) return state + " · 采样时间未知";
        if (at > now.AddSeconds(5)) return state + " · 采样时间异常";
        if (!cached && now - at > TimeSpan.FromMinutes(2)) state += " · 旧读数";
        return state + $" · {at.ToLocalTime():HH:mm:ss}";
    }

    private void SetAudioWaiting()
    {
        _outputs.IsEnabled = _volume.IsEnabled = _outputMute.IsEnabled = _microphoneMute.IsEnabled = false;
        _volumeText.Text = "—";
        _microphoneState.Text = "正在读取系统音频状态…";
    }

    private void FitWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (work.IsEmpty) return;
        MinWidth = Math.Min(MinWidth, Math.Max(240, work.Width));
        MinHeight = Math.Min(MinHeight, Math.Max(200, work.Height));
        Width = Math.Min(Width, work.Width);
        Height = Math.Min(Height, work.Height);
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - ActualWidth));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    private static TextBlock Caption(string text)
    {
        var result = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        result.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return result;
    }

    private static Button ActionButton(string text)
    {
        var button = new Button { Content = text, Padding = new(10, 6, 10, 6), FontSize = 12 };
        AutomationProperties.SetName(button, text);
        return button;
    }

    private static Border Divider() => new Border { Height = 1, Margin = new(0, 15, 0, 14) }.Resource(Border.BackgroundProperty, "Stroke");
    private static void ConfigureError(TextBlock text) { text.FontSize = 11; text.Margin = new(0, 7, 0, 0); text.SetResourceReference(TextBlock.ForegroundProperty, "Warning"); text.Visibility = Visibility.Collapsed; }
    private static void SetError(TextBlock text, string? error) { text.Text = error ?? ""; text.ToolTip = error; text.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible; }
    private static string DeviceIcon(string category) => category switch { "鼠标" => "\uE962", "键盘" => "\uE765", "耳机" or "音频设备" => "\uE7F6", "麦克风" => "\uE720", "手柄" => "\uE7FC", _ => "\uE950" };
}

internal static class HubUiResource
{
    internal static T Resource<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
