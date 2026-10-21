// Perivolt · 阳影X (YangYingX) · https://github.com/YangYingX/Perivolt
// Licensing and historical permissions: repository LICENSE and NOTICE.md.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PowerHub.Models;
using PowerHub.Plugins;
using PowerHub.Services;

namespace Perivolt.TaskbarTools;

public sealed class TaskbarToolsPlugin : IInteractivePowerHubPlugin
{
    private const int MissingDeviceLimit = 12;
    private readonly Dictionary<string, SeenDevice> _seen = new(StringComparer.OrdinalIgnoreCase);
    private DeviceStatus[] _devices = [];
    private PluginUiContext? _context;
    private DateTimeOffset _scannedAt;
    private long _sequence;
    private long _wheelRemainder;
    private string? _selectedDeviceId;
    private bool _selectionKnown;
    private bool _disposed;
    private Window? _window;
    private OverviewView? _windowView;
    private WeakReference<OverviewView>? _popupView;

    public void Initialize(PluginUiContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _context = context;
    }

    public void Update(PluginUiSnapshot snapshot)
    {
        if (_disposed) return;
        _scannedAt = snapshot.ScannedAt;
        _devices = snapshot.Devices.Where(device => !string.IsNullOrWhiteSpace(device.Id))
            .DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var currentIds = _devices.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _devices) _seen[device.Id] = new(device, ++_sequence);
        foreach (var id in _seen.Where(entry => !currentIds.Contains(entry.Key))
            .OrderByDescending(entry => entry.Value.Order).Skip(MissingDeviceLimit).Select(entry => entry.Key).ToArray())
            _seen.Remove(id);
        RefreshViews();
    }

    public FrameworkElement? CreateTaskbarPopup(string? selectedDeviceId)
    {
        if (_disposed || _context is null) return null;
        _selectedDeviceId = selectedDeviceId;
        _selectionKnown = true;
        if (_popupView?.TryGetTarget(out var previous) == true) previous.Release();
        var view = new OverviewView(this, popup: true);
        _popupView = new(view);
        view.Refresh();
        return view;
    }

    public bool HandleTaskbarWheel(string? selectedDeviceId, int delta)
    {
        if (_disposed || _context is null || delta == 0) return false;
        var devices = SelectableDevices();
        if (devices.Length == 0) { _wheelRemainder = 0; return false; }
        _selectedDeviceId = selectedDeviceId;
        _selectionKnown = true;
        _wheelRemainder += delta;
        var steps = _wheelRemainder / 120;
        _wheelRemainder %= 120;
        if (steps == 0) return true;
        var index = Array.FindIndex(devices, device => SameId(device.Id, selectedDeviceId));
        var distance = Math.Abs(steps);
        if (index < 0) { index = 0; distance--; }
        var direction = steps > 0 ? -1 : 1;
        index = (int)((index + direction * (distance % devices.Length) + devices.Length) % devices.Length);
        SelectDevice(devices[index].Id);
        return true;
    }

    public void Open()
    {
        if (_disposed || _context is null) return;
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            return;
        }
        _windowView = new(this, popup: false) { Margin = new Thickness(12) };
        _windowView.Refresh();
        _window = new Window
        {
            Title = L.T("任务栏增强") + " · Perivolt", Width = 460, Height = 440,
            MinWidth = 330, MinHeight = 260, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Microsoft YaHei UI"), Content = _windowView
        };
        _window.SetResourceReference(Window.BackgroundProperty, "PageBackground");
        _window.SetResourceReference(Window.ForegroundProperty, "TextPrimary");
        _window.Closed += OnWindowClosed;
        _window.Show();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context = null;
        _wheelRemainder = 0;
        if (_popupView?.TryGetTarget(out var popup) == true) popup.Release();
        _popupView = null;
        _windowView?.Release();
        _windowView = null;
        if (_window is { } window)
        {
            _window = null;
            window.Closed -= OnWindowClosed;
            window.Content = null;
            window.Close();
        }
        _devices = [];
        _seen.Clear();
        _selectedDeviceId = null;
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        if (sender is Window window) { window.Closed -= OnWindowClosed; window.Content = null; }
        _windowView?.Release();
        _windowView = null;
        _window = null;
    }

    private void RefreshViews()
    {
        if (_popupView?.TryGetTarget(out var popup) == true) popup.Refresh();
        if (_windowView?.IsVisible == true) _windowView.Refresh();
    }

    private DeviceStatus[] SelectableDevices() => _devices.Where(CanSelect)
        .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    private void SelectDevice(string? id)
    {
        if (_disposed || _context is null || id is not null && !_devices.Any(device => SameId(device.Id, id) && CanSelect(device))) return;
        _context.SelectTaskbarDevice(id);
        if (_disposed) return;
        _selectedDeviceId = id;
        _selectionKnown = true;
        RefreshViews();
    }

    private static bool SameId(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool HasBattery(DeviceStatus device) => device.BatteryPercent is >= 0 and <= 100 || !string.IsNullOrWhiteSpace(device.BatteryLevel);
    private static bool CanSelect(DeviceStatus device) => device.ConnectionKnown && device.IsConnected && HasBattery(device);
    private static bool Cached(DeviceStatus device) => device.Source.Contains("Windows", StringComparison.OrdinalIgnoreCase) && device.Source.Contains("属性", StringComparison.Ordinal) ||
        device.Source.Contains("缓存", StringComparison.Ordinal) || device.Message.Contains("缓存", StringComparison.Ordinal);
    private static bool Old(DeviceStatus device, DateTimeOffset now) => device.UpdatedAt is { } updated && now - updated > TimeSpan.FromMinutes(5);
    private static string BatteryLabel(DeviceStatus device) => device.BatteryPercent is >= 0 and <= 100
        ? device.BatteryPercent.Value + "%" : !string.IsNullOrWhiteSpace(device.BatteryLevel) ? L.T(device.BatteryLevel) : L.T("未读到");

    private static string ReadingState(DeviceStatus device, bool missing, DateTimeOffset now)
    {
        var parts = new List<string> { L.T(missing ? "离线（本次扫描未出现）" : !device.ConnectionKnown ? "连接未知" : device.IsConnected ? "已连接" : "离线") };
        if (!HasBattery(device)) parts.Add(L.T("未读到电量"));
        else
        {
            if (Cached(device)) parts.Add(L.T("系统属性 / 缓存读数"));
            if (device.UpdatedAt is null) parts.Add(L.T("采样时间未知"));
            else if (device.UpdatedAt > now.AddMinutes(1)) parts.Add(L.T("读数时间异常"));
            else if (Old(device, now)) parts.Add(L.T("旧读数"));
            else if (!Cached(device)) parts.Add(L.T("最近一次读数"));
            if (!missing && device.ConnectionKnown && device.IsConnected && device.IsCharging == true) parts.Add(L.T("正在充电"));
        }
        return string.Join(" · ", parts);
    }

    private static string ReadingTime(DeviceStatus device)
    {
        if (device.UpdatedAt is not { } updated) return L.T("采样时间未知");
        var label = Cached(device) ? L.T("属性读取") : L.T(HasBattery(device) ? "读数时间" : "状态时间");
        return label + " " + updated.ToLocalTime().ToString("MM-dd HH:mm:ss") + (Cached(device) ? " · " + L.T("设备报告时间未知") : "");
    }

    private sealed record SeenDevice(DeviceStatus Device, long Order);

    private sealed class OverviewView : Border
    {
        private static readonly ControlTemplate DeviceButtonTemplate = CreateDeviceButtonTemplate();
        private TaskbarToolsPlugin? _owner;
        private readonly TextBlock _scanTime;
        private readonly TextBlock _selection;
        private readonly StackPanel _rows = new();
        private readonly Button _automatic;
        private readonly List<Button> _deviceButtons = [];

        public OverviewView(TaskbarToolsPlugin owner, bool popup)
        {
            _owner = owner;
            Padding = new Thickness(12);
            CornerRadius = new CornerRadius(12);
            BorderThickness = new Thickness(1);
            if (popup) { Width = 360; MaxHeight = 390; }
            SetResourceReference(BackgroundProperty, "Surface");
            SetResourceReference(BorderBrushProperty, "Stroke");
            var grid = new Grid();
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            _automatic = new Button { Content = L.T("自动"), Padding = new Thickness(10, 5, 10, 5), FontSize = 12, ToolTip = L.T("自动显示最低电量设备") };
            AutomationProperties.SetName(_automatic, L.T("自动显示最低电量设备"));
            _automatic.Click += OnAutomatic;
            DockPanel.SetDock(_automatic, Dock.Right);
            header.Children.Add(_automatic);
            header.Children.Add(Text(L.T("设备电量概览"), 16, "TextPrimary", FontWeights.SemiBold));
            grid.Children.Add(header);
            var details = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            _scanTime = Text("", 11, "TextSecondary");
            _selection = Text("", 11, "TextSecondary");
            _selection.Margin = new Thickness(0, 4, 0, 0);
            details.Children.Add(_scanTime); details.Children.Add(_selection);
            Grid.SetRow(details, 1); grid.Children.Add(details);
            var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 2); grid.Children.Add(scroll);
            var hint = Text(L.T("点击已连接且有电量的设备切换任务栏显示；滚轮每格切换。"), 11, "TextSecondary");
            hint.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(hint, 3); grid.Children.Add(hint);
            Child = grid;
        }

        public void Refresh()
        {
            if (_owner is not { } owner) return;
            foreach (var button in _deviceButtons) button.Click -= OnDevice;
            _deviceButtons.Clear(); _rows.Children.Clear();
            _scanTime.Text = owner._scannedAt == default ? L.T("尚未收到设备扫描") : L.T("最近扫描") + " · " + owner._scannedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
            var selected = owner._devices.FirstOrDefault(device => SameId(device.Id, owner._selectedDeviceId))?.Name;
            _selection.Text = !owner._selectionKnown ? L.T("点击设备可切换任务栏显示") : owner._selectedDeviceId is null
                ? L.T("任务栏：自动显示最低电量设备") : L.T("任务栏设备") + " · " + (selected ?? L.T("已保存设备（当前不可用）"));
            var now = DateTimeOffset.UtcNow;
            var currentIds = owner._devices.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var device in owner._devices.OrderByDescending(CanSelect).ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase))
                AddRow(device, missing: false, now);
            var missing = owner._seen.Values.Where(item => !currentIds.Contains(item.Device.Id)).OrderByDescending(item => item.Order).ToArray();
            if (missing.Length > 0)
            {
                var label = Text(L.T("本次运行曾出现（最多保留 12 台）"), 11, "TextSecondary");
                label.Margin = new Thickness(0, 10, 0, 6); _rows.Children.Add(label);
                foreach (var item in missing) AddRow(item.Device, missing: true, now);
            }
            if (owner._devices.Length == 0 && missing.Length == 0)
                _rows.Children.Add(Text(L.T("尚未发现设备；等待宿主下次扫描。"), 13, "TextSecondary"));
        }

        private void AddRow(DeviceStatus device, bool missing, DateTimeOffset now)
        {
            var selectable = !missing && CanSelect(device);
            var selected = _owner!._selectionKnown && SameId(device.Id, _owner._selectedDeviceId);
            var content = new StackPanel();
            var title = new Grid();
            title.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var name = Text(device.Name, 13, "TextPrimary", FontWeights.SemiBold);
            name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = device.Name; name.Margin = new Thickness(0, 0, 10, 0);
            title.Children.Add(name);
            var battery = BatteryLabel(device);
            if ((missing || !device.ConnectionKnown || !device.IsConnected) && HasBattery(device)) battery = L.T("上次") + " " + battery;
            var value = Text(battery, 14, selectable && !Old(device, now) ? "Accent" : "TextSecondary", FontWeights.SemiBold);
            Grid.SetColumn(value, 1); title.Children.Add(value); content.Children.Add(title);
            var state = ReadingState(device, missing, now);
            var stateText = Text(state, 11, "TextSecondary"); stateText.Margin = new Thickness(0, 5, 0, 0); content.Children.Add(stateText);
            var time = Text(device.Source + " · " + ReadingTime(device), 10, "TextSecondary"); time.Margin = new Thickness(0, 3, 0, 0); content.Children.Add(time);
            if (selectable)
            {
                var button = new Button { Content = content, Tag = device.Id, Template = DeviceButtonTemplate, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6), ToolTip = device.Message };
                button.SetResourceReference(BackgroundProperty, "SurfaceAlt");
                button.SetResourceReference(BorderBrushProperty, selected ? "Accent" : "Stroke");
                AutomationProperties.SetName(button, device.Name + "，" + battery + "，" + state);
                button.Click += OnDevice; _deviceButtons.Add(button); _rows.Children.Add(button);
            }
            else
            {
                var border = new Border { Child = content, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6), ToolTip = device.Message };
                border.SetResourceReference(BackgroundProperty, "SurfaceAlt"); border.SetResourceReference(BorderBrushProperty, selected ? "Accent" : "Stroke");
                _rows.Children.Add(border);
            }
        }

        private void OnAutomatic(object sender, RoutedEventArgs args) => _owner?.SelectDevice(null);
        private void OnDevice(object sender, RoutedEventArgs args) { if (sender is Button { Tag: string id }) _owner?.SelectDevice(id); }
        private static ControlTemplate CreateDeviceButtonTemplate()
        {
            var frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            frame.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
            template.Triggers.Add(new Trigger { Property = IsMouseOverProperty, Value = true, Setters = { new Setter(OpacityProperty, .85) } });
            template.Triggers.Add(new Trigger { Property = Button.IsPressedProperty, Value = true, Setters = { new Setter(OpacityProperty, .7) } });
            return template;
        }
        public void Release()
        {
            _automatic.Click -= OnAutomatic;
            foreach (var button in _deviceButtons) button.Click -= OnDevice;
            _deviceButtons.Clear(); _rows.Children.Clear(); _owner = null; Child = null; Resources.Clear();
        }
        private static TextBlock Text(string value, double size, string resource, FontWeight? weight = null)
        {
            var text = new TextBlock { Text = value, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, resource);
            return text;
        }
    }
}
