using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PowerHub.Models;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace PowerHub;

public partial class DesktopWidgetWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _settingsChanged;
    private readonly Action _openDashboard;
    private readonly DispatcherTimer _positionSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _applyingSettings;
    private bool _closingForExit;

    public DesktopWidgetWindow(AppSettings settings, Action settingsChanged, Action openDashboard)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _openDashboard = openDashboard;
        InitializeComponent();
        _positionSaveTimer.Tick += (_, _) => SavePosition();
        Loaded += (_, _) => ApplySettings();
        LocationChanged += (_, _) =>
        {
            if (_applyingSettings || !IsLoaded || !IsVisible || _closingForExit) return;
            _positionSaveTimer.Stop();
            _positionSaveTimer.Start();
        };
        SizeChanged += (_, _) => { if (IsLoaded && !_applyingSettings) ClampToScreen(); };
        Closing += OnClosing;
        ApplySettings();
    }

    public void UpdateDevices(IReadOnlyList<DeviceStatus> devices)
    {
        var pinned = devices.Where(device => device.ConnectionKnown && device.IsConnected && _settings.IsPinned(device.Id));
        var items = pinned.Select(device =>
        {
            var appearance = new DeviceCard(device, _settings);
            var name = appearance.Name;
            var percent = device.BatteryPercent is int value ? Math.Clamp(value, 0, 100) : (int?)null;
            var needsCharge = device.IsCharging != true && (percent is int remaining ? remaining <= _settings.GetThreshold(device.Id) : device.IsBatteryLow == true);
            var status = device.IsCharging == true ? "正在充电" : _settings.WidgetCompact
                ? needsCharge ? "电量偏低" : "" : $"已连接 · {device.Connection}";
            var label = percent is int battery ? $"{battery}%" : _settings.WidgetCompact ? device.BatteryLevel ?? "—" : "—";
            var advice = _settings.WidgetCompact ? "" : needsCharge ? "该充电了" : !percent.HasValue && device.BatteryLevel is not null
                ? $"电量：{device.BatteryLevel}" : !device.HasBattery ? "暂未获取电量" : "";
            return new WidgetDevice(name, status, advice, percent, label,
                percent.HasValue ? $"{name}，{label}，{status}" : $"{name}，电量未知，{status}",
                needsCharge, needsCharge && percent is <= 10, _settings.DisplayStyle,
                _settings.AnimationsEnabled, _settings.WidgetCompact, _settings.WidgetTransparent,
                appearance.NameBrush, appearance.BatteryBrush, device.IsCharging == true, appearance.IconGeometry);
        }).ToList();
        DeviceItems.ItemsSource = items;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var lowCount = items.Count(item => item.NeedsCharge);
        SummaryText.Text = items.Count == 0 ? "连接固定的外设后显示电量" : lowCount > 0
            ? $"{items.Count} 个设备 · {lowCount} 个需要充电" : $"{items.Count} 个常用设备";
    }

    public void ApplySettings()
    {
        _applyingSettings = true;
        try
        {
            Width = _settings.WidgetCompact ? 260 : 312;
            WidgetSurface.Padding = _settings.WidgetCompact ? new Thickness(12, 10, 12, 8) : new Thickness(16, 14, 16, 12);
            DragHeader.Margin = new Thickness(0, 0, 0, _settings.WidgetCompact ? 8 : 12);
            SummaryText.Visibility = _settings.WidgetCompact ? Visibility.Collapsed : Visibility.Visible;
            FooterText.Visibility = _settings.WidgetCompact ? Visibility.Collapsed : Visibility.Visible;
            EmptyExplanation.Visibility = _settings.WidgetCompact ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Padding = _settings.WidgetCompact ? new Thickness(12, 14, 12, 10) : new Thickness(16, 20, 16, 20);
            WidgetShadow.Opacity = _settings.WidgetTransparent ? 0 : 0.14;
            if (_settings.WidgetTransparent)
            {
                WidgetSurface.Background = System.Windows.Media.Brushes.Transparent;
                EmptyState.Background = System.Windows.Media.Brushes.Transparent;
            }
            else
            {
                WidgetSurface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Surface");
                EmptyState.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SurfaceAlt");
            }
            EmptyState.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Stroke");
            EmptyState.BorderThickness = new Thickness(_settings.WidgetTransparent ? 1 : 0);
            Topmost = _settings.WidgetTopmost;
            DragHeader.Cursor = _settings.WidgetLocked ? Cursors.Arrow : Cursors.SizeAll;
            TopmostMenuItem.IsChecked = _settings.WidgetTopmost;
            LockedMenuItem.IsChecked = _settings.WidgetLocked;
            FooterText.Text = $"PowerHub · {(_settings.NotificationsEnabled ? "充电提醒已开启" : "充电提醒已关闭")}";
            if (IsLoaded)
            {
                var area = SystemParameters.WorkArea;
                Left = _settings.WidgetLeft is double left && double.IsFinite(left) ? left : area.Right - Width - 20;
                Top = _settings.WidgetTop is double top && double.IsFinite(top) ? top : area.Top + 20;
                ClampToScreen();
            }
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    public void CloseForExit()
    {
        _closingForExit = true;
        _positionSaveTimer.Stop();
        Close();
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.WidgetLocked || e.ButtonState != MouseButtonState.Pressed) return;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != DragHeader; source = VisualTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        DragMove();
        ClampToScreen();
        SavePosition();
    }

    private void ClampToScreen()
    {
        // WPF positions use device-independent units; scale desktop working areas into the same space.
        var dpi = VisualTreeHelper.GetDpi(this);
        var areas = System.Windows.Forms.Screen.AllScreens.Select(screen => new Rect(
            screen.WorkingArea.Left / dpi.DpiScaleX, screen.WorkingArea.Top / dpi.DpiScaleY,
            screen.WorkingArea.Width / dpi.DpiScaleX, screen.WorkingArea.Height / dpi.DpiScaleY)).ToArray();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 200;
        var center = new Point(Left + width / 2, Top + height / 2);
        var area = areas.OrderBy(candidate =>
        {
            var x = Math.Clamp(center.X, candidate.Left, candidate.Right);
            var y = Math.Clamp(center.Y, candidate.Top, candidate.Bottom);
            return (x - center.X) * (x - center.X) + (y - center.Y) * (y - center.Y);
        }).FirstOrDefault(SystemParameters.WorkArea);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - height));
    }

    private void SavePosition()
    {
        _positionSaveTimer.Stop();
        if (!IsLoaded || _closingForExit) return;
        _settings.WidgetLeft = Left;
        _settings.WidgetTop = Top;
        _settingsChanged();
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        TopmostMenuItem.IsChecked = _settings.WidgetTopmost;
        LockedMenuItem.IsChecked = _settings.WidgetLocked;
    }

    private void OnTopmostClick(object sender, RoutedEventArgs e)
    {
        _settings.WidgetTopmost = TopmostMenuItem.IsChecked;
        Topmost = _settings.WidgetTopmost;
        _settingsChanged();
    }

    private void OnLockClick(object sender, RoutedEventArgs e)
    {
        _settings.WidgetLocked = LockedMenuItem.IsChecked;
        DragHeader.Cursor = _settings.WidgetLocked ? Cursors.Arrow : Cursors.SizeAll;
        _settingsChanged();
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e) => _openDashboard();

    private void OnHideWidget(object sender, RoutedEventArgs e) => HideWidget();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingForExit) return;
        e.Cancel = true;
        HideWidget();
    }

    private void HideWidget()
    {
        SavePosition();
        _settings.WidgetEnabled = false;
        Hide();
        _settingsChanged();
    }

    private sealed record WidgetDevice(string Name, string Status, string Advice, int? Percent,
        string BatteryLabel, string BatteryDescription, bool NeedsCharge, bool IsCritical,
        string DisplayStyle, bool AnimationsEnabled, bool Compact, bool Transparent,
        System.Windows.Media.Brush NameBrush, System.Windows.Media.Brush BatteryBrush, bool IsCharging,
        System.Windows.Media.Geometry IconGeometry);
}
