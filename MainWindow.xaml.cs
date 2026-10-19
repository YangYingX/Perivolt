using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PowerHub.Models;
using PowerHub.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace PowerHub;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private bool _updating;
    private int _cardsRevision = -1;
    private List<DeviceCard> _cards = [];
    private string? _lastSearch;
    private bool _lastBatteryOnly;
    private readonly DispatcherTimer _thresholdSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };

    public MainWindow(AppController controller)
    {
        _controller = controller;
        _updating = true;
        InitializeComponent();
        BatteryColorPanel.Initialize(_controller);
        UpdatePanel.Initialize(_controller);
        PluginPanel.Initialize(_controller);
        RepeatCombo.ItemsSource = new[] { 15, 30, 60, 120, 240 }.Select(value => new Choice(value, $"每 {value} 分钟"));
        PollCombo.ItemsSource = new[] { 30, 60, 120, 300, 600 }.Select(value => new Choice(value, value < 60 ? $"每 {value} 秒" : $"每 {value / 60} 分钟"));
        _updating = false;
        _controller.Changed += UpdateView;
        _thresholdSaveTimer.Tick += (_, _) =>
        {
            if (ThresholdSlider.IsMouseCaptureWithin) return;
            _thresholdSaveTimer.Stop();
            CommitDefaultThreshold();
        };
        ThresholdSlider.LostMouseCapture += (_, _) =>
        {
            if (_updating || ThresholdSlider.IsMouseCaptureWithin || !_thresholdSaveTimer.IsEnabled) return;
            _thresholdSaveTimer.Stop();
            CommitDefaultThreshold();
        };
        Closed += (_, _) =>
        {
            _controller.Changed -= UpdateView;
            _thresholdSaveTimer.Stop();
            CommitDefaultThreshold();
        };
        UpdateView();
        Navigate("devices");
    }

    public void Navigate(string page)
    {
        var selected = page is "settings" or "widgets" or "plugins" ? page : "devices";
        DevicesPage.Visibility = selected == "devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = selected == "settings" ? Visibility.Visible : Visibility.Collapsed;
        WidgetsPage.Visibility = selected == "widgets" ? Visibility.Visible : Visibility.Collapsed;
        PluginsPage.Visibility = selected == "plugins" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, target) in new[] { (DevicesNav, "devices"), (SettingsNav, "settings"), (WidgetNav, "widgets"), (PluginsNav, "plugins") })
        {
            button.SetResourceReference(BackgroundProperty, selected == target ? "AccentSoft" : "Surface");
            button.SetResourceReference(ForegroundProperty, selected == target ? "Accent" : "TextSecondary");
            button.FontWeight = selected == target ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void UpdateView()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateView)); return; }
        _updating = true;
        try
        {
            var settings = _controller.Settings;
            var devices = _controller.Devices.Where(device => device.ConnectionKnown && device.IsConnected).ToList();
            var readable = devices.Count(device => device.HasBattery);
            var low = devices.Count(device => device.ConnectionKnown && device.IsConnected && device.IsCharging != true &&
                (device.BatteryPercent is int percent ? percent <= settings.GetThreshold(device.Id) : device.IsBatteryLow == true));
            TotalText.Text = devices.Count.ToString();
            ReadableText.Text = readable.ToString();
            LowText.Text = low.ToString();
            CoverageRing.Percent = devices.Count == 0 ? null : (int)Math.Round(100d * readable / devices.Count);
            CoverageText.Text = devices.Count == 0 ? "—" : $"{CoverageRing.Percent}%";
            UpdateText.Text = _controller.StatusText;
            RefreshButton.IsEnabled = !_controller.IsScanning;
            RefreshButton.Content = _controller.IsScanning ? "正在更新…" : "↻  刷新电量";
            var cardsChanged = _cardsRevision != _controller.ViewRevision;
            if (cardsChanged)
            {
                _cards = devices.OrderByDescending(device => device.HasBattery)
                    .Select(device => new DeviceCard(device, settings)).ToList();
                _cardsRevision = _controller.ViewRevision;
                DeviceSettingsItems.ItemsSource = _cards;
                WidgetDeviceItems.ItemsSource = _cards;
            }
            var cards = _cards;
            var search = DeviceSearch.Text.Trim();
            var batteryOnly = BatteryOnlyBox.IsChecked == true;
            var visible = cards.Where(card => (BatteryOnlyBox.IsChecked != true || card.Status.HasBattery) &&
                (search.Length == 0 || $"{card.Name} {card.Status.Name} {card.Subtitle} {card.Status.Manufacturer} {card.Status.HardwareText}".Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
            if (cardsChanged || search != _lastSearch || batteryOnly != _lastBatteryOnly)
                DeviceItems.ItemsSource = visible;
            _lastSearch = search;
            _lastBatteryOnly = batteryOnly;
            NoWidgetDevices.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoDeviceSettings.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            WidgetSelectionSummary.Text = $"已固定 {cards.Count(card => settings.IsPinned(card.Id))} 台在线设备";
            NoDevices.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoDevicesTitle.Text = devices.Count == 0 ? "当前没有已连接的外设" : "没有符合条件的设备";
            SearchHint.Visibility = search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            NotificationsBox.IsChecked = settings.NotificationsEnabled;
            if (!_thresholdSaveTimer.IsEnabled) ThresholdSlider.Value = settings.LowBatteryThreshold;
            UpdateThresholdPreview();
            RepeatCombo.SelectedValue = settings.RepeatReminderMinutes;
            PollCombo.SelectedValue = settings.PollIntervalSeconds;
            SystemTheme.IsChecked = settings.Theme == "System";
            LightTheme.IsChecked = settings.Theme == "Light";
            DarkTheme.IsChecked = settings.Theme == "Dark";
            OceanTheme.IsChecked = settings.Theme == "Ocean";
            LavenderTheme.IsChecked = settings.Theme == "Lavender";
            SunsetTheme.IsChecked = settings.Theme == "Sunset";
            ForestTheme.IsChecked = settings.Theme == "Forest";
            BatteryColorPanel.Refresh();
            PluginPanel.Refresh();
            RingStyle.IsChecked = settings.DisplayStyle == "Ring";
            BarStyle.IsChecked = settings.DisplayStyle == "Bar";
            HourglassStyle.IsChecked = settings.DisplayStyle == "Hourglass";
            OceanStyle.IsChecked = settings.DisplayStyle == "Ocean";
            AnimationsBox.IsChecked = settings.AnimationsEnabled;
            RingPreview.AnimationsEnabled = settings.AnimationsEnabled;
            BarPreview.AnimationsEnabled = settings.AnimationsEnabled;
            HourglassPreview.AnimationsEnabled = settings.AnimationsEnabled;
            OceanPreview.AnimationsEnabled = settings.AnimationsEnabled;
            foreach (var preview in new[] { RingPreview, BarPreview, HourglassPreview, OceanPreview, WidgetPreviewVisual })
                preview.Foreground = BatteryPalette.GetBrush(65);
            WidgetBox.IsChecked = settings.WidgetEnabled;
            TopmostBox.IsChecked = settings.WidgetTopmost;
            LockedBox.IsChecked = settings.WidgetLocked;
            WidgetCompactBox.IsChecked = settings.WidgetCompact;
            WidgetTransparentBox.IsChecked = settings.WidgetTransparent;
            WidgetPreviewVisual.DisplayStyle = settings.DisplayStyle;
            WidgetPreviewVisual.AnimationsEnabled = settings.AnimationsEnabled;
            var styleName = settings.DisplayStyle switch { "Ring" => "圆环", "Bar" => "条形", "Hourglass" => "沙漏", _ => "海洋球" };
            WidgetStyleText.Text = $"{styleName} · {(settings.AnimationsEnabled ? "动效已开启" : "动效已关闭")}";
            StartInTrayBox.IsChecked = settings.StartInTray;
            StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        }
        finally { _updating = false; }
    }

    private void UpdateThresholdPreview()
    {
        var threshold = (int)Math.Round(ThresholdSlider.Value);
        ThresholdText.Text = $"电量 ≤ {threshold}%";
        ThresholdPreview.Text = $"{threshold}%";
        ThresholdRing.Percent = threshold;
    }

    private void Apply(Action<AppSettings> change)
    {
        if (_updating) return;
        change(_controller.Settings);
        _controller.ApplySettings();
        SettingsStatusText.Text = "设置已更新，会自动保存。";
        WidgetStatusText.Text = "设置已更新，会自动保存。";
    }

    private void OnDevicesNav(object sender, RoutedEventArgs e) => Navigate("devices");
    private void OnFilterChanged(object sender, RoutedEventArgs e) { if (!_updating) UpdateView(); }
    private void OnSettingsNav(object sender, RoutedEventArgs e) => Navigate("settings");
    private void OnWidgetNav(object sender, RoutedEventArgs e) => Navigate("widgets");
    private void OnPluginsNav(object sender, RoutedEventArgs e) => Navigate("plugins");
    private void OnWidgetAppearance(object sender, RoutedEventArgs e)
    {
        Navigate("settings");
        Dispatcher.BeginInvoke(new Action(() => DisplayStyleSection.BringIntoView()), DispatcherPriority.Loaded);
    }
    private void OnMinimize(object sender, RoutedEventArgs e) => Close();
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private async void OnRefresh(object sender, RoutedEventArgs e) => await _controller.RefreshAsync();
    private void OnPinDevice(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) _controller.TogglePin(id); }
    private void OnNotificationsClick(object sender, RoutedEventArgs e) => Apply(settings => settings.NotificationsEnabled = NotificationsBox.IsChecked == true);
    private void OnWidgetClick(object sender, RoutedEventArgs e) { if (!_updating) _controller.ToggleWidget(); }
    private void OnTopmostClick(object sender, RoutedEventArgs e) => Apply(settings => settings.WidgetTopmost = TopmostBox.IsChecked == true);
    private void OnLockedClick(object sender, RoutedEventArgs e) => Apply(settings => settings.WidgetLocked = LockedBox.IsChecked == true);
    private void OnWidgetCompactClick(object sender, RoutedEventArgs e) => Apply(settings => settings.WidgetCompact = WidgetCompactBox.IsChecked == true);
    private void OnWidgetTransparentClick(object sender, RoutedEventArgs e) => Apply(settings => settings.WidgetTransparent = WidgetTransparentBox.IsChecked == true);
    private void OnStartInTrayClick(object sender, RoutedEventArgs e) => Apply(settings => settings.StartInTray = StartInTrayBox.IsChecked == true);
    private void OnStartWithWindowsClick(object sender, RoutedEventArgs e)
    {
        var enabled = StartWithWindowsBox.IsChecked == true;
        Apply(settings => settings.StartWithWindows = enabled);
        if (_controller.Settings.StartWithWindows != enabled) SettingsStatusText.Text = _controller.StatusText;
    }
    private void OnThemeClick(object sender, RoutedEventArgs e) { if (sender is RadioButton { Tag: string theme }) Apply(settings => settings.Theme = theme); }
    private void OnDisplayStyleClick(object sender, RoutedEventArgs e) { if (sender is RadioButton { Tag: string style }) Apply(settings => settings.DisplayStyle = style); }
    private void OnAnimationsClick(object sender, RoutedEventArgs e) => Apply(settings => settings.AnimationsEnabled = AnimationsBox.IsChecked == true);
    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || ThresholdPreview is null) return;
        UpdateThresholdPreview();
        _thresholdSaveTimer.Stop();
        _thresholdSaveTimer.Start();
    }
    private void CommitDefaultThreshold()
    {
        var value = (int)Math.Round(ThresholdSlider.Value);
        if (_controller.Settings.LowBatteryThreshold != value) Apply(settings => settings.LowBatteryThreshold = value);
    }
    private void OnRepeatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepeatCombo.SelectedValue is int value) Apply(settings => settings.RepeatReminderMinutes = value);
    }
    private void OnPollChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PollCombo.SelectedValue is int value) Apply(settings => settings.PollIntervalSeconds = value);
    }
    private void OnDeviceAlertClick(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not CheckBox { Tag: string id } box) return;
        _controller.GetPreference(id).NotificationsEnabled = box.IsChecked == true;
        _controller.ApplySettings();
    }
    private void OnNameColorClick(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not Button { Tag: string id, DataContext: DeviceCard card }) return;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (card.NameBrush is System.Windows.Media.SolidColorBrush brush)
            dialog.Color = System.Drawing.Color.FromArgb(brush.Color.R, brush.Color.G, brush.Color.B);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var preference = _controller.GetPreference(id);
        Apply(_ => preference.NameColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }
    private void OnResetNameColor(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not Button { Tag: string id }) return;
        var preference = _controller.GetPreference(id);
        Apply(_ => preference.NameColor = null);
    }
    private void OnDeviceNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) SaveDeviceName(box);
    }
    private void OnDeviceNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        SaveDeviceName(box);
        e.Handled = true;
    }
    private void SaveDeviceName(TextBox box)
    {
        if (_updating || box.Tag is not string id || box.DataContext is not DeviceCard card) return;
        var name = AppSettings.NormalizeCustomName(box.Text);
        if (name == card.Status.Name) name = null;
        var preference = _controller.GetPreference(id);
        box.Text = name ?? card.Status.Name;
        if (preference.CustomName == name) return;
        Apply(_ => preference.CustomName = name);
    }
    private void OnDeviceThresholdLostFocus(object sender, RoutedEventArgs e) { if (sender is TextBox box) SaveDeviceThreshold(box); }
    private void OnDeviceThresholdKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        SaveDeviceThreshold(box);
        e.Handled = true;
    }
    private void SaveDeviceThreshold(TextBox box)
    {
        if (_updating || box.Tag is not string id) return;
        var text = box.Text.Trim();
        int? threshold = null;
        if (text.Length > 0)
        {
            if (!int.TryParse(text, out var value) || value is < 1 or > 100)
            {
                ThresholdErrorText.Text = "请输入 1–100 的整数，或留空使用默认提醒阈值。";
                ThresholdErrorText.Visibility = Visibility.Visible;
                box.SetResourceReference(BorderBrushProperty, "Danger");
                return;
            }
            threshold = value;
        }
        ThresholdErrorText.Visibility = Visibility.Collapsed;
        box.SetResourceReference(BorderBrushProperty, "Stroke");
        var preference = _controller.GetPreference(id);
        if (preference.LowBatteryThreshold == threshold) return;
        preference.LowBatteryThreshold = threshold;
        _controller.ApplySettings();
    }
    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出兼容性诊断", Filter = "JSON 文件 (*.json)|*.json", FileName = $"PowerHub-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.json" };
        if (dialog.ShowDialog(this) != true) return;
        ExportButton.IsEnabled = false;
        try { await _controller.ExportAsync(dialog.FileName); SettingsStatusText.Text = "诊断文件已导出。"; }
        catch (Exception error) { SettingsStatusText.Text = $"导出未完成：{error.Message}"; }
        finally { ExportButton.IsEnabled = true; }
    }

    private sealed record Choice(int Value, string Label)
    {
        public override string ToString() => Label;
    }
}
