using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PowerHub.Controls;
using PowerHub.Models;
using PowerHub.Services;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;

namespace PowerHub;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private bool _updating;
    private bool _refreshing;
    private int _cardsRevision = -1;
    private List<DeviceCard> _cards = [];
    private List<DeviceChoice> _choices = [];
    private string? _selectedDeviceId;
    private string _settingsTab = "reminders";
    private string? _lastSearch;
    private bool _lastBatteryOnly;
    private bool _thresholdDirty;
    private bool _visualOptionsDirty;
    private bool _opacityDirty;
    private bool _intensityDirty;
    private string _opacityStyle = "Ring";
    private Point _dragStart;
    private string? _dragDeviceId;
    private readonly DispatcherTimer _thresholdSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private readonly DispatcherTimer _visualSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };

    public MainWindow(AppController controller)
    {
        _controller = controller;
        _updating = true;
        InitializeComponent();
        BatteryColorPanel.Initialize(controller);
        BatteryColorPanel.PreviewChanged += (_, _) => UpdateStylePreviews();
        UpdatePanel.Initialize(controller);
        PluginPanel.Initialize(controller);
        PollCombo.ItemsSource = new[] { 30, 60, 120, 300, 600 }.Select(value => new Choice(value, value < 60 ? $"每 {value} 秒" : $"每 {value / 60} 分钟"));
        _thresholdSaveTimer.Tick += (_, _) => { if (!ThresholdSlider.IsMouseCaptureWithin) CommitThreshold(); };
        _visualSaveTimer.Tick += (_, _) => { if (!OpacitySlider.IsMouseCaptureWithin && !IntensitySlider.IsMouseCaptureWithin) CommitVisualOptions(); };
        ThresholdSlider.LostMouseCapture += (_, _) => { if (!_updating && !ThresholdSlider.IsMouseCaptureWithin) CommitThreshold(); };
        OpacitySlider.LostMouseCapture += (_, _) => { if (!_updating && !OpacitySlider.IsMouseCaptureWithin) CommitVisualOptions(); };
        IntensitySlider.LostMouseCapture += (_, _) => { if (!_updating && !IntensitySlider.IsMouseCaptureWithin) CommitVisualOptions(); };
        _updating = false;
        controller.Changed += UpdateView;
        StateChanged += (_, _) => MaximizeIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M3,1 H11 V9 H9 M1,3 H9 V11 H1 Z" : "M1,1 H11 V11 H1 Z");
        Closed += (_, _) =>
        {
            controller.Changed -= UpdateView;
            CommitPending();
            _thresholdSaveTimer.Stop();
            _visualSaveTimer.Stop();
        };
        UpdateView();
        Navigate("devices");
    }

    public void Navigate(string page)
    {
        CommitPending();
        var selected = page is "settings" or "widgets" or "appearance" or "general" ? "settings" : page == "plugins" ? "plugins" : "devices";
        DevicesPage.Visibility = selected == "devices" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = selected == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PluginsPage.Visibility = selected == "plugins" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, target) in new[] { (DevicesNav, "devices"), (SettingsNav, "settings"), (PluginsNav, "plugins") })
        {
            button.SetResourceReference(BackgroundProperty, selected == target ? "AccentSoft" : "Surface");
            button.SetResourceReference(ForegroundProperty, selected == target ? "Accent" : "TextSecondary");
            button.FontWeight = selected == target ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (page is "widgets" or "appearance" or "general") SelectSettingsTab(page == "widgets" ? "desktop" : page, false);
        else if (selected == "settings") SelectSettingsTab(_settingsTab, false);
    }

    private void UpdateView()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateView)); return; }
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var settings = _controller.Settings;
            var devices = _controller.Devices.Where(device => device.ConnectionKnown && device.IsConnected).ToList();
            var scopeChanged = _selectedDeviceId is not null && !devices.Any(device => device.Id == _selectedDeviceId);
            if (scopeChanged) { CommitPending(); _selectedDeviceId = null; }
            _updating = true;
            var readable = devices.Count(device => device.HasBattery);
            var low = devices.Count(device => device.IsCharging != true &&
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
                _cards = devices.OrderBy(device => settings.GetDeviceOrder(device.Id)).ThenByDescending(device => device.HasBattery)
                    .Select(device => new DeviceCard(device, settings)).ToList();
                _cardsRevision = _controller.ViewRevision;
                DesktopDeviceItems.ItemsSource = _cards;
            }
            var search = DeviceSearch.Text.Trim();
            var batteryOnly = BatteryOnlyBox.IsChecked == true;
            var visible = _cards.Where(card => (!batteryOnly || card.Status.HasBattery) &&
                (search.Length == 0 || $"{card.Name} {card.Status.Name} {card.Subtitle} {card.Status.Manufacturer} {card.Status.HardwareText}".Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
            if (cardsChanged || search != _lastSearch || batteryOnly != _lastBatteryOnly) DeviceItems.ItemsSource = visible;
            _lastSearch = search;
            _lastBatteryOnly = batteryOnly;
            NoDevices.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoDevicesTitle.Text = devices.Count == 0 ? "当前没有已连接的外设" : "没有符合条件的设备";
            SearchHint.Visibility = search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var choices = new List<DeviceChoice> { new(null, "默认配置") };
            choices.AddRange(_cards.Select(card => new DeviceChoice(card.Id, card.Name)));
            if (!_choices.SequenceEqual(choices))
            {
                _choices = choices;
                SettingsDeviceSelector.ItemsSource = _choices;
                SettingsDeviceSelector.SelectedItem = _choices.First(choice => choice.Id == _selectedDeviceId);
            }
            if (scopeChanged) BatteryColorPanel.SetDevice(null);
            RefreshPreferences(scopeChanged);
            PluginPanel.Refresh();
        }
        finally { _updating = false; _refreshing = false; }
    }

    private DevicePreference? Preference => _selectedDeviceId is not null && _controller.Settings.Devices.TryGetValue(_selectedDeviceId, out var preference) ? preference : null;

    private void RefreshPreferences(bool force = false)
    {
        var settings = _controller.Settings;
        var preference = Preference;
        var device = _cards.FirstOrDefault(card => card.Id == _selectedDeviceId);
        var showDeviceScope = _settingsTab is "reminders" or "appearance";
        SettingsScopeRow.Visibility = SettingsLivePreview.Visibility = showDeviceScope ? Visibility.Visible : Visibility.Collapsed;
        SettingsTitle.Margin = new Thickness(0, 0, showDeviceScope ? 152 : 0, 20);
        ScopeHint.Text = _selectedDeviceId is null
            ? "默认值适用于未单独配置的设备。" : "仅设置所选设备，断开连接后仍会记住。";
        ReminderScopeText.Text = _selectedDeviceId is null ? "设置所有设备的提醒总开关与默认值。" : "为这台设备设置独立提醒；重置后继续跟随默认配置。";
        NotificationsBox.IsChecked = _selectedDeviceId is null ? settings.NotificationsEnabled : preference?.NotificationsEnabled ?? true;
        GlobalReminderHint.Text = "默认配置中的提醒总开关已关闭；此设备的提醒设置会保留。";
        GlobalReminderHint.Visibility = _selectedDeviceId is not null && !settings.NotificationsEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (force || !_thresholdDirty && !ThresholdBox.IsKeyboardFocusWithin)
        {
            ThresholdSlider.Value = _selectedDeviceId is null ? settings.LowBatteryThreshold : settings.GetThreshold(_selectedDeviceId);
            ThresholdBox.Text = ((int)ThresholdSlider.Value).ToString();
        }
        UpdateThresholdPreview();
        var repeat = settings.GetRepeatReminderMinutes(_selectedDeviceId);
        var repeatChoices = new[] { 15, 30, 60, 120, 240, repeat }.Distinct().Order().ToArray();
        if (RepeatCombo.ItemsSource is not List<Choice> currentRepeats || !currentRepeats.Select(choice => choice.Value).SequenceEqual(repeatChoices))
            RepeatCombo.ItemsSource = repeatChoices.Select(value => new Choice(value, $"每 {value} 分钟")).ToList();
        RepeatCombo.SelectedValue = repeat;
        ResetRemindersButton.Visibility = _selectedDeviceId is null ? Visibility.Collapsed : Visibility.Visible;
        DeviceIdentityCard.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
        if (force || !DeviceNameBox.IsKeyboardFocusWithin) DeviceNameBox.Text = device?.Name ?? "";
        NameColorSwatch.Background = device?.NameBrush;
        var style = settings.GetDisplayStyle(_selectedDeviceId);
        RingStyle.IsChecked = style == "Ring";
        BarStyle.IsChecked = style == "Bar";
        HourglassStyle.IsChecked = style == "Hourglass";
        OceanStyle.IsChecked = style == "Ocean";
        AnimationsBox.IsChecked = settings.GetAnimationsEnabled(_selectedDeviceId);
        PercentageBox.IsChecked = settings.GetShowBatteryPercentage(_selectedDeviceId);
        StyleExpander.AnimationsEnabled = ColorsExpander.AnimationsEnabled = settings.AnimationsEnabled;
        if (force || !_visualOptionsDirty && !OpacityBox.IsKeyboardFocusWithin && !IntensityBox.IsKeyboardFocusWithin)
        {
            _opacityStyle = style;
            OpacitySlider.Value = Math.Round(settings.GetFillOpacity(_selectedDeviceId, style) * 100);
            IntensitySlider.Value = Math.Round(settings.GetAnimationIntensity(_selectedDeviceId) * 100);
            OpacityBox.Text = ((int)OpacitySlider.Value).ToString();
            IntensityBox.Text = ((int)IntensitySlider.Value).ToString();
        }
        OpacityControls.Visibility = style == "Ring" ? Visibility.Collapsed : Visibility.Visible;
        OpacityTitle.Text = $"{StyleLabel(style)}填充透明度 · 每种样式分别保存";
        UpdateStylePreviews();
        BatteryColorPanel.Refresh();
        ResetAppearanceButton.Visibility = _selectedDeviceId is null ? Visibility.Collapsed : Visibility.Visible;
        WidgetBox.IsChecked = settings.WidgetEnabled;
        TopmostBox.IsChecked = settings.WidgetTopmost;
        LockedBox.IsChecked = settings.WidgetLocked;
        WidgetCompactBox.IsChecked = settings.WidgetCompact;
        WidgetTransparentBox.IsChecked = settings.WidgetTransparent;
        DesktopDevicesEmptyText.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PollCombo.SelectedValue = settings.PollIntervalSeconds;
        foreach (var (radio, theme) in new[] { (SystemTheme,"System"),(LightTheme,"Light"),(DarkTheme,"Dark"),(OceanTheme,"Ocean"),(LavenderTheme,"Lavender"),(SunsetTheme,"Sunset"),(ForestTheme,"Forest") }) radio.IsChecked = settings.Theme == theme;
        StartInTrayBox.IsChecked = settings.StartInTray;
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        var pluginThemes = _controller.Plugins.AvailableThemes.Select(entry => new PluginThemeChoice("plugin:" + entry.Id, entry.Name, settings.Theme == "plugin:" + entry.Id)).ToList();
        PluginThemeItems.ItemsSource = pluginThemes;
        PluginThemeCard.Visibility = pluginThemes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var liveWidget = _controller.Widget is { IsVisible: true } widget ? widget.Content as Visual : null;
        WidgetMirrorBrush.Visual = liveWidget;
        WidgetLivePreview.Visibility = liveWidget is null ? Visibility.Collapsed : Visibility.Visible;
        WidgetPreviewEmptyText.Visibility = liveWidget is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string StyleLabel(string style) => style switch { "Ring" => "圆环", "Bar" => "条形", "Hourglass" => "沙漏", _ => "海洋球" };

    private void UpdateStylePreviews()
    {
        var settings = _controller.Settings;
        var device = _cards.FirstOrDefault(card => card.Id == _selectedDeviceId);
        var percent = _selectedDeviceId is null ? 65 : device?.Status.BatteryPercent;
        var label = _selectedDeviceId is null ? "65%" : device?.Status.BatteryText ?? "—";
        var brush = BatteryColorPanel.PreviewBrush;
        AppearancePreviewName.Text = device?.Name ?? "默认外观";
        AppearancePreviewName.Foreground = device?.NameBrush ?? TryFindResource("TextPrimary") as System.Windows.Media.Brush;
        AppearancePreviewHint.Text = _selectedDeviceId is null ? "示例电量 65% · 默认配置将应用于未单独设置的设备" : $"当前设备电量 {label} · 下方样式使用实时读数预览";
        foreach (var (preview, style) in new[] { (RingPreview,"Ring"),(BarPreview,"Bar"),(HourglassPreview,"Hourglass"),(OceanPreview,"Ocean") })
        {
            preview.Foreground = brush;
            preview.Percent = percent;
            preview.IsCharging = device?.IsCharging == true;
            preview.AnimationsEnabled = settings.GetAnimationsEnabled(_selectedDeviceId);
            preview.FillOpacity = _visualOptionsDirty && _opacityStyle == style ? OpacitySlider.Value / 100 : settings.GetFillOpacity(_selectedDeviceId, style);
            preview.ColorOpacity = BatteryColorPanel.PreviewColorOpacity;
            preview.AnimationIntensity = _visualOptionsDirty ? IntensitySlider.Value / 100 : settings.GetAnimationIntensity(_selectedDeviceId);
        }
        foreach (var number in new[] { RingPreviewNumber, BarPreviewNumber, HourglassPreviewNumber, OceanPreviewNumber })
        {
            number.Text = label;
            number.Visibility = percent is null || settings.GetShowBatteryPercentage(_selectedDeviceId) ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateSettingsPreview();
    }

    private void UpdateSettingsPreview()
    {
        var settings = _controller.Settings;
        var device = _cards.FirstOrDefault(card => card.Id == _selectedDeviceId);
        var style = settings.GetDisplayStyle(_selectedDeviceId);
        var percent = _selectedDeviceId is null ? 65 : device?.Status.BatteryPercent;
        var label = _selectedDeviceId is null ? "65%" : device?.Status.HasBattery == true ? device.Status.BatteryText : "未知";
        SettingsPreviewName.Text = device?.Name ?? "默认外观";
        SettingsPreviewName.Foreground = device?.NameBrush ?? TryFindResource("TextPrimary") as System.Windows.Media.Brush;
        SettingsPreviewVisual.Percent = percent;
        SettingsPreviewVisual.DisplayStyle = style;
        SettingsPreviewGraphic.Width = style == "Hourglass" ? 92 : 72;
        SettingsPreviewVisual.Width = style == "Hourglass" ? 52 : 72;
        SettingsPreviewVisual.HorizontalAlignment = style == "Hourglass" ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Center;
        SettingsPreviewVisual.Foreground = BatteryColorPanel.PreviewBrush;
        SettingsPreviewVisual.IsCharging = device?.IsCharging == true;
        SettingsPreviewVisual.AnimationsEnabled = settings.GetAnimationsEnabled(_selectedDeviceId);
        SettingsPreviewVisual.FillOpacity = _visualOptionsDirty && _opacityStyle == style ? OpacitySlider.Value / 100 : settings.GetFillOpacity(_selectedDeviceId, style);
        SettingsPreviewVisual.ColorOpacity = BatteryColorPanel.PreviewColorOpacity;
        SettingsPreviewVisual.AnimationIntensity = _visualOptionsDirty ? IntensitySlider.Value / 100 : settings.GetAnimationIntensity(_selectedDeviceId);
        SettingsPreviewNumber.Text = label;
        SettingsPreviewNumber.HorizontalAlignment = style == "Hourglass" ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Center;
        SettingsPreviewNumber.Visibility = percent is null || settings.GetShowBatteryPercentage(_selectedDeviceId) ? Visibility.Visible : Visibility.Collapsed;
        SettingsPreviewHint.Text = _selectedDeviceId is null ? "示例电量" : device?.IsCharging == true ? "正在充电" : "实时外观";
        SettingsPreviewGraphic.ToolTip = $"{SettingsPreviewName.Text} · {label}";
    }

    private void Apply(Action<AppSettings> change)
    {
        if (_updating) return;
        change(_controller.Settings);
        _controller.ApplySettings();
        SettingsStatusText.Text = "设置已更新，会自动保存。";
    }

    private void CommitPending() { CommitThreshold(); CommitVisualOptions(); BatteryColorPanel.CommitPending(); }
    private void OnSettingsDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || SettingsDeviceSelector.SelectedItem is not DeviceChoice choice || choice.Id == _selectedDeviceId) return;
        CommitPending();
        _selectedDeviceId = choice.Id;
        _thresholdDirty = _visualOptionsDirty = false;
        _opacityDirty = _intensityDirty = false;
        BatteryColorPanel.SetDevice(choice.Id);
        _updating = true;
        try { RefreshPreferences(true); }
        finally { _updating = false; }
    }
    private void OnDeviceSettings(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        Navigate("settings");
        SettingsDeviceSelector.SelectedItem = _choices.FirstOrDefault(choice => choice.Id == id) ?? _choices[0];
        SelectSettingsTab("appearance", true);
    }
    private void OnSettingsTabClick(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string tab }) SelectSettingsTab(tab, true); }
    private void SelectSettingsTab(string tab, bool animate)
    {
        CommitPending();
        _settingsTab = tab;
        var tabs = new[] { ("reminders",RemindersTab,ReminderTabButton),("appearance",AppearanceTab,AppearanceTabButton),("desktop",DesktopTab,DesktopTabButton),("general",GeneralTab,GeneralTabButton) };
        foreach (var (name, content, button) in tabs)
        {
            content.Visibility = name == tab ? Visibility.Visible : Visibility.Collapsed;
            button.SetResourceReference(ForegroundProperty, name == tab ? "Accent" : "TextSecondary");
            button.FontWeight = name == tab ? FontWeights.SemiBold : FontWeights.Normal;
            content.BeginAnimation(OpacityProperty, null);
            if (name == tab && animate && _controller.Settings.AnimationsEnabled)
                content.BeginAnimation(OpacityProperty, new DoubleAnimation(.6, 1, TimeSpan.FromMilliseconds(150)) { FillBehavior = FillBehavior.Stop });
        }
        UpdateTabIndicator(animate);
        _updating = true;
        try { RefreshPreferences(); }
        finally { _updating = false; }
    }
    private void OnSettingsTabSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabIndicator(false);
    private void UpdateTabIndicator(bool animate)
    {
        if (SettingsTabIndicator is null) return;
        var width = SettingsTabBar.ActualWidth / 4;
        SettingsTabIndicator.Width = width;
        var index = Array.IndexOf(new[] { "reminders", "appearance", "desktop", "general" }, _settingsTab);
        var target = width * Math.Max(0, index);
        var from = SettingsTabTranslation.X;
        SettingsTabTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        SettingsTabTranslation.X = target;
        if (animate && _controller.Settings.AnimationsEnabled)
            SettingsTabTranslation.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    private void UpdateThresholdPreview()
    {
        var value = (int)Math.Round(ThresholdSlider.Value);
        ThresholdRing.Percent = value;
        ThresholdPreview.Text = $"{value}%";
        ThresholdText.Text = $"电量 ≤ {value}% 时提醒；充电中与离线时暂停。";
    }
    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || ThresholdBox is null) return;
        _updating = true; ThresholdBox.Text = ((int)Math.Round(ThresholdSlider.Value)).ToString(); _updating = false;
        ThresholdErrorText.Visibility = Visibility.Collapsed;
        UpdateThresholdPreview();
        _thresholdDirty = true;
        _thresholdSaveTimer.Stop(); _thresholdSaveTimer.Start();
    }
    private void OnThresholdInput(object sender, TextChangedEventArgs e)
    {
        if (_updating || ThresholdSlider is null) return;
        _thresholdSaveTimer.Stop();
        if (!int.TryParse(ThresholdBox.Text, out var value) || value is < 1 or > 100)
        { ThresholdErrorText.Text = "请输入 1–100 的整数。"; ThresholdErrorText.Visibility = Visibility.Visible; return; }
        _updating = true; ThresholdSlider.Value = value; _updating = false;
        ThresholdErrorText.Visibility = Visibility.Collapsed;
        UpdateThresholdPreview(); _thresholdDirty = true; _thresholdSaveTimer.Start();
    }
    private void OnThresholdInputLostFocus(object sender, RoutedEventArgs e) => CommitThreshold();
    private void OnThresholdInputKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitThreshold(); e.Handled = true; } }
    private void CommitThreshold()
    {
        _thresholdSaveTimer.Stop();
        if (_updating || !_thresholdDirty || !int.TryParse(ThresholdBox.Text, out var value) || value is < 1 or > 100) return;
        _thresholdDirty = false;
        if (_selectedDeviceId is null) { if (_controller.Settings.LowBatteryThreshold != value) Apply(settings => settings.LowBatteryThreshold = value); }
        else { var preference = _controller.GetPreference(_selectedDeviceId); if (preference.LowBatteryThreshold != value) Apply(_ => preference.LowBatteryThreshold = value); }
    }
    private void OnNotificationsClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDeviceId is null) Apply(settings => settings.NotificationsEnabled = NotificationsBox.IsChecked == true);
        else Apply(_ => _controller.GetPreference(_selectedDeviceId).NotificationsEnabled = NotificationsBox.IsChecked == true);
    }
    private void OnRepeatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || RepeatCombo.SelectedValue is not int value) return;
        if (_selectedDeviceId is null) Apply(settings => settings.RepeatReminderMinutes = value);
        else Apply(_ => _controller.GetPreference(_selectedDeviceId).RepeatReminderMinutes = value);
    }
    private void OnResetReminders(object sender, RoutedEventArgs e)
    {
        if (_selectedDeviceId is null) return;
        _thresholdDirty = false; _thresholdSaveTimer.Stop();
        Apply(_ => { var preference = _controller.GetPreference(_selectedDeviceId); preference.LowBatteryThreshold = null; preference.RepeatReminderMinutes = null; preference.NotificationsEnabled = true; });
    }

    private void OnDeviceNameLostFocus(object sender, RoutedEventArgs e) => SaveDeviceName();
    private void OnDeviceNameKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { SaveDeviceName(); e.Handled = true; } }
    private void SaveDeviceName()
    {
        if (_updating || _selectedDeviceId is null) return;
        var device = _cards.FirstOrDefault(card => card.Id == _selectedDeviceId);
        var name = AppSettings.NormalizeCustomName(DeviceNameBox.Text);
        if (name == device?.Status.Name) name = null;
        var preference = _controller.GetPreference(_selectedDeviceId);
        DeviceNameBox.Text = name ?? device?.Status.Name ?? "";
        if (preference.CustomName != name) Apply(_ => preference.CustomName = name);
    }
    private void OnNameColorClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDeviceId is null) return;
        var initial = NameColorSwatch.Background is SolidColorBrush brush ? brush.Color.ToString() : null;
        if (ColorPickerDialog.Pick(this, initial, "设备名称颜色") is { } color) Apply(_ => _controller.GetPreference(_selectedDeviceId).NameColor = color);
    }
    private void OnResetIdentity(object sender, RoutedEventArgs e)
    {
        if (_selectedDeviceId is null) return;
        Apply(_ => { var preference = _controller.GetPreference(_selectedDeviceId); preference.CustomName = null; preference.NameColor = null; });
    }
    private void OnDisplayStyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string style }) return;
        CommitVisualOptions();
        _visualSaveTimer.Stop();
        _visualOptionsDirty = _opacityDirty = _intensityDirty = false;
        if (_selectedDeviceId is null) Apply(settings => settings.DisplayStyle = style);
        else Apply(_ => _controller.GetPreference(_selectedDeviceId).DisplayStyle = style);
        _updating = true;
        try { RefreshPreferences(true); }
        finally { _updating = false; }
    }
    private void OnAnimationsClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDeviceId is null) Apply(settings => settings.AnimationsEnabled = AnimationsBox.IsChecked == true);
        else Apply(_ => _controller.GetPreference(_selectedDeviceId).AnimationsEnabled = AnimationsBox.IsChecked == true);
    }
    private void OnPercentageClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDeviceId is null) Apply(settings => settings.ShowBatteryPercentage = PercentageBox.IsChecked == true);
        else Apply(_ => _controller.GetPreference(_selectedDeviceId).ShowBatteryPercentage = PercentageBox.IsChecked == true);
    }
    private void OnVisualOptionSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || OpacityBox is null || IntensityBox is null) return;
        _updating = true;
        OpacityBox.Text = ((int)Math.Round(OpacitySlider.Value)).ToString(); IntensityBox.Text = ((int)Math.Round(IntensitySlider.Value)).ToString();
        _updating = false;
        _opacityDirty |= ReferenceEquals(sender, OpacitySlider);
        _intensityDirty |= ReferenceEquals(sender, IntensitySlider);
        VisualOptionError.Visibility = Visibility.Collapsed; _visualOptionsDirty = true; UpdateStylePreviews();
        _visualSaveTimer.Stop(); _visualSaveTimer.Start();
    }
    private void OnVisualOptionInput(object sender, TextChangedEventArgs e)
    {
        if (_updating || OpacitySlider is null || IntensitySlider is null) return;
        _visualSaveTimer.Stop();
        if (!TryVisualOptions(out var opacity, out var intensity)) return;
        _updating = true; OpacitySlider.Value = opacity; IntensitySlider.Value = intensity; _updating = false;
        _opacityDirty |= ReferenceEquals(sender, OpacityBox);
        _intensityDirty |= ReferenceEquals(sender, IntensityBox);
        _visualOptionsDirty = true; UpdateStylePreviews(); _visualSaveTimer.Start();
    }
    private bool TryVisualOptions(out int opacity, out int intensity)
    {
        var opacityValid = int.TryParse(OpacityBox.Text, out opacity) && opacity is >= 10 and <= 100;
        var intensityValid = int.TryParse(IntensityBox.Text, out intensity) && intensity is >= 50 and <= 300;
        VisualOptionError.Text = "透明度须为 10–100，动效幅度须为 50–300。";
        VisualOptionError.Visibility = opacityValid && intensityValid ? Visibility.Collapsed : Visibility.Visible;
        return opacityValid && intensityValid;
    }
    private void OnVisualOptionLostFocus(object sender, RoutedEventArgs e) => CommitVisualOptions();
    private void OnVisualOptionKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitVisualOptions(); e.Handled = true; } }
    private void CommitVisualOptions()
    {
        _visualSaveTimer.Stop();
        if (_updating || !_visualOptionsDirty || !TryVisualOptions(out var opacity, out var intensity)) return;
        _visualOptionsDirty = false;
        var opacityChanged = _opacityDirty;
        var intensityChanged = _intensityDirty;
        _opacityDirty = _intensityDirty = false;
        Apply(settings =>
        {
            if (_selectedDeviceId is null)
            {
                if (intensityChanged) settings.AnimationIntensity = intensity / 100d;
                if (opacityChanged && _opacityStyle == "Bar") settings.BarOpacity = opacity / 100d;
                else if (opacityChanged && _opacityStyle == "Hourglass") settings.HourglassOpacity = opacity / 100d;
                else if (opacityChanged && _opacityStyle == "Ocean") settings.OceanOpacity = opacity / 100d;
            }
            else
            {
                var preference = _controller.GetPreference(_selectedDeviceId);
                if (intensityChanged) preference.AnimationIntensity = intensity / 100d;
                if (opacityChanged && _opacityStyle == "Bar") preference.BarOpacity = opacity / 100d;
                else if (opacityChanged && _opacityStyle == "Hourglass") preference.HourglassOpacity = opacity / 100d;
                else if (opacityChanged && _opacityStyle == "Ocean") preference.OceanOpacity = opacity / 100d;
            }
        });
    }
    private void OnResetAppearance(object sender, RoutedEventArgs e)
    {
        if (_selectedDeviceId is null) return;
        _visualOptionsDirty = _opacityDirty = _intensityDirty = false; _visualSaveTimer.Stop(); BatteryColorPanel.DiscardPending();
        Apply(_ => { var preference = _controller.GetPreference(_selectedDeviceId); preference.DisplayStyle = null; preference.AnimationsEnabled = null; preference.ShowBatteryPercentage = null; preference.BatteryColors = null; preference.BatteryColorOpacity = null; preference.BarOpacity = null; preference.HourglassOpacity = null; preference.OceanOpacity = null; preference.AnimationIntensity = null; });
        BatteryColorPanel.SetDevice(_selectedDeviceId, true);
    }

    private void OnDesktopDevicePin(object sender, RoutedEventArgs e) { if (!_updating && sender is System.Windows.Controls.CheckBox { Tag: string id }) _controller.TogglePin(id); }
    private void OnWidgetClick(object sender, RoutedEventArgs e) { if (!_updating) _controller.ToggleWidget(); }
    private void OnTopmostClick(object sender, RoutedEventArgs e) => Apply(settings => settings.WidgetTopmost = TopmostBox.IsChecked == true);
    private void OnLockedClick(object sender, RoutedEventArgs e) => Apply(settings => settings.WidgetLocked = LockedBox.IsChecked == true);
    private void OnWidgetCompactClick(object sender, RoutedEventArgs e) => Apply(settings => settings.WidgetCompact = WidgetCompactBox.IsChecked == true);
    private void OnWidgetTransparentClick(object sender, RoutedEventArgs e) => Apply(settings => settings.WidgetTransparent = WidgetTransparentBox.IsChecked == true);
    private void OnRecoverWidget(object sender, RoutedEventArgs e) { Apply(settings => { settings.WidgetEnabled = true; settings.WidgetLocked = false; }); _controller.Widget?.ResetPosition(); SettingsStatusText.Text = "已找回卡片到主屏并解锁，外观与置顶设置已保留。"; }
    private void OnPollChanged(object sender, SelectionChangedEventArgs e) { if (PollCombo.SelectedValue is int value) Apply(settings => settings.PollIntervalSeconds = value); }
    private void OnStartInTrayClick(object sender, RoutedEventArgs e) => Apply(settings => settings.StartInTray = StartInTrayBox.IsChecked == true);
    private void OnStartWithWindowsClick(object sender, RoutedEventArgs e) { var enabled = StartWithWindowsBox.IsChecked == true; Apply(settings => settings.StartWithWindows = enabled); if (_controller.Settings.StartWithWindows != enabled) SettingsStatusText.Text = _controller.StatusText; }
    private void OnThemeClick(object sender, RoutedEventArgs e) { if (sender is RadioButton { Tag: string theme }) Apply(settings => settings.Theme = theme); }
    private void OnDevicesNav(object sender, RoutedEventArgs e) => Navigate("devices");
    private void OnSettingsNav(object sender, RoutedEventArgs e) => Navigate("settings");
    private void OnPluginsNav(object sender, RoutedEventArgs e) => Navigate("plugins");
    private void OnFilterChanged(object sender, RoutedEventArgs e) { if (!_updating) UpdateView(); }
    private void OnMinimize(object sender, RoutedEventArgs e) => Close();
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private async void OnRefresh(object sender, RoutedEventArgs e) => await _controller.RefreshAsync();
    private void OnPinDevice(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) _controller.TogglePin(id); }
    private void OnDeviceDragStart(object sender, MouseButtonEventArgs e) { _dragStart = e.GetPosition(this); _dragDeviceId = (sender as FrameworkElement)?.Tag as string; }
    private void OnDeviceDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragDeviceId is null || sender is not DependencyObject handle) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var id = _dragDeviceId; _dragDeviceId = null;
        System.Windows.DragDrop.DoDragDrop(handle, new System.Windows.DataObject("PowerHub.DeviceOrder", id), System.Windows.DragDropEffects.Move);
    }
    private void OnDeviceDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("PowerHub.DeviceOrder") ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None; e.Handled = true; }
    private void OnDeviceDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceCard target } targetElement || e.Data.GetData("PowerHub.DeviceOrder") is not string source ||
            string.Equals(source, target.Id, StringComparison.OrdinalIgnoreCase) || !_cards.Any(card => string.Equals(card.Id, source, StringComparison.OrdinalIgnoreCase))) return;
        var order = _controller.Settings.DeviceOrder.Concat(_cards.Select(card => card.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        order.RemoveAll(id => string.Equals(id, source, StringComparison.OrdinalIgnoreCase));
        var targetIndex = order.FindIndex(id => string.Equals(id, target.Id, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0) return;
        var afterTarget = e.GetPosition(targetElement).Y >= targetElement.ActualHeight / 2;
        order.Insert(targetIndex + (afterTarget ? 1 : 0), source);
        Apply(settings => settings.DeviceOrder = order);
        e.Handled = true;
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
    private sealed record Choice(int Value, string Label);
    private sealed record DeviceChoice(string? Id, string Label);
    private sealed record PluginThemeChoice(string Value, string Label, bool Selected);
}
