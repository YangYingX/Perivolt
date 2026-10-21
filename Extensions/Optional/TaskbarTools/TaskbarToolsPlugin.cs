// Perivolt · 阳影X (YangYingX) · https://github.com/YangYingX/Perivolt
// Licensing and historical permissions: repository LICENSE and NOTICE.md.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    private volatile bool _disposed;
    private Window? _window;
    private OverviewView? _windowView;
    private readonly List<WeakReference<OverviewView>> _popupViews = [];
    private readonly Dictionary<string, (IPluginTaskbarTarget Target, IPluginTaskbarSurface Surface)> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _uiGate = new();
    private Dispatcher? _dispatcher;
    private DispatcherOperation? _stateOperation;
    private DispatcherTimer? _topologyTimer;
    private NativeTaskbarTargets? _topology;
    private TaskbarPreferenceStore? _store;
    private TaskbarPreferences _preferences = new();
    private bool _preferencesReady;
    private bool _taskbarEnabled;
    private bool _refreshingSurfaces;
    private string? _storageError;
    private string? _managerError;

    public void Initialize(PluginUiContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_context is not null) throw new InvalidOperationException("Plugin is already initialized");
        _context = context;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _store = new TaskbarPreferenceStore(context.DataDirectory, message => PostState(() => { _storageError = message; RefreshViews(); }));
        _ = _store.Loading.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) PostState(() =>
            {
                _preferences = task.Result;
                _preferencesReady = true;
                SyncTaskbars();
                RefreshViews();
            });
            else { _ = task.Exception; PostState(() => { _storageError ??= "无法读取多屏设置，请检查插件数据目录。多屏显示暂未启用。"; RefreshViews(); }); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Update(PluginUiSnapshot snapshot)
    {
        if (_disposed) return;
        _scannedAt = snapshot.ScannedAt;
        _taskbarEnabled = snapshot.TaskbarEnabled;
        _selectedDeviceId = snapshot.TaskbarDeviceId;
        _selectionKnown = true;
        _devices = snapshot.Devices.Where(device => !string.IsNullOrWhiteSpace(device.Id))
            .DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var currentIds = _devices.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _devices) _seen[device.Id] = new(device, ++_sequence);
        foreach (var id in _seen.Where(entry => !currentIds.Contains(entry.Key))
            .OrderByDescending(entry => entry.Value.Order).Skip(MissingDeviceLimit).Select(entry => entry.Key).ToArray())
            _seen.Remove(id);
        SyncTaskbars();
        RefreshViews();
    }

    public FrameworkElement? CreateTaskbarPopup(string? selectedDeviceId)
    {
        if (_disposed || _context is null) return null;
        _selectedDeviceId = selectedDeviceId;
        _selectionKnown = true;
        var view = new OverviewView(this, popup: true);
        _popupViews.RemoveAll(reference => !reference.TryGetTarget(out _));
        _popupViews.Add(new(view));
        view.Unloaded += OnPopupUnloaded;
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
        StopTaskbars();
        lock (_uiGate) { _stateOperation?.Abort(); _stateOperation = null; }
        _store?.Dispose();
        _context = null;
        _wheelRemainder = 0;
        foreach (var reference in _popupViews.ToArray()) if (reference.TryGetTarget(out var popup)) ReleasePopup(popup);
        _popupViews.Clear();
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

    public Task FlushAsync(CancellationToken cancellationToken) => _store?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    private void PostState(Action action)
    {
        lock (_uiGate)
        {
            if (_disposed || _dispatcher is null || _dispatcher.HasShutdownStarted) return;
            _stateOperation?.Abort();
            try { _stateOperation = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                lock (_uiGate) _stateOperation = null;
                if (!_disposed) { try { action(); } catch (Exception) { TaskbarFailure(); } }
            })); }
            catch (Exception) { _stateOperation = null; }
        }
    }

    private void SyncTaskbars()
    {
        if (_refreshingSurfaces) return;
        if (_disposed || !_taskbarEnabled || !_preferencesReady || _context is null || _dispatcher is null)
        { StopTaskbars(); return; }
        _refreshingSurfaces = true;
        try
        {
            if (_topology is null)
            {
                _topology = new NativeTaskbarTargets(_dispatcher, ReadOffset, SaveOffset, () =>
                { if (!_disposed && _taskbarEnabled) _context?.RefreshTaskbar?.Invoke(); }, TaskbarFailure);
                _topologyTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(2) };
                _topologyTimer.Tick += OnTopologyTick;
                _topologyTimer.Start();
            }
            _topology.Refresh();
            var targets = _preferences.AllDisplays
                ? _topology.SecondaryTargets.ToDictionary(entry => entry.Key, entry => (IPluginTaskbarTarget)entry.Value, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, IPluginTaskbarTarget>(StringComparer.OrdinalIgnoreCase);
            ReconcileSurfaces(targets);
        }
        catch (Exception) { TaskbarFailure(); }
        finally { _refreshingSurfaces = false; }
    }

    private void ReconcileSurfaces(IReadOnlyDictionary<string, IPluginTaskbarTarget> targets)
    {
        foreach (var entry in _surfaces.ToArray())
        {
            if (targets.TryGetValue(entry.Key, out var target) && ReferenceEquals(target, entry.Value.Target) && !entry.Value.Surface.IsDisposed) continue;
            entry.Value.Surface.Dispose();
            _surfaces.Remove(entry.Key);
        }
        _managerError = null;
        foreach (var entry in targets)
        {
            try
            {
                if (!_surfaces.TryGetValue(entry.Key, out var owned))
                {
                    var surface = _context?.CreateTaskbarSurface?.Invoke(entry.Value);
                    if (surface is null) continue;
                    owned = (entry.Value, surface);
                    _surfaces.Add(entry.Key, owned);
                }
                owned.Surface.UpdateDevices(_devices);
                if (owned.Surface.LastError is not null) _managerError = "部分副屏任务栏暂不可用，等待恢复。";
            }
            catch (Exception) { TaskbarFailure(); return; }
        }
    }

    private void OnTopologyTick(object? sender, EventArgs args)
    { try { SyncTaskbars(); RefreshViews(); } catch (Exception) { TaskbarFailure(); } }

    private void TaskbarFailure()
    {
        StopTaskbars();
        _managerError = "多屏任务栏暂不可用，副屏显示已停止。";
        try { RefreshViews(); } catch (Exception) { }
    }

    private void StopTaskbars()
    {
        if (_topologyTimer is { } timer) { timer.Stop(); timer.Tick -= OnTopologyTick; _topologyTimer = null; }
        foreach (var owned in _surfaces.Values) { try { owned.Surface.Dispose(); } catch (Exception) { } }
        _surfaces.Clear();
        try { _topology?.Dispose(); } catch (Exception) { }
        _topology = null;
        _managerError = null;
    }

    private int ReadOffset(string monitor) => _preferences.MonitorOffsets.TryGetValue(monitor, out var offset) ? offset : 0;

    private void SaveOffset(string monitor, int offset)
    {
        if (_disposed || !_preferencesReady || string.IsNullOrWhiteSpace(monitor) || monitor.Length > 128) return;
        var offsets = new Dictionary<string, int>(_preferences.MonitorOffsets, StringComparer.OrdinalIgnoreCase);
        offsets.Remove(monitor);
        if (offsets.Count >= 32) offsets.Remove(offsets.Keys.First());
        offsets[monitor] = Math.Clamp(offset, 0, 32767);
        _preferences = _preferences with { MonitorOffsets = offsets };
        _store?.Save(_preferences);
    }

    private void SetAllDisplays(bool enabled)
    {
        if (_disposed || !_preferencesReady || _preferences.AllDisplays == enabled) return;
        _preferences = _preferences with { AllDisplays = enabled };
        _store?.Save(_preferences);
        SyncTaskbars();
        RefreshViews();
    }

    private void OnPopupUnloaded(object sender, RoutedEventArgs args) { if (sender is OverviewView view) ReleasePopup(view); }
    private void ReleasePopup(OverviewView view)
    {
        view.Unloaded -= OnPopupUnloaded;
        view.Release();
        _popupViews.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, view));
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
        foreach (var reference in _popupViews.ToArray()) if (reference.TryGetTarget(out var popup)) popup.Refresh();
        _popupViews.RemoveAll(reference => !reference.TryGetTarget(out _));
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
        private readonly CheckBox? _allDisplays;
        private readonly TextBlock? _multiStatus;
        private bool _updatingDisplayOption;

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
            if (!popup)
            {
                var options = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                _allDisplays = new CheckBox { Content = L.T("在所有显示器的任务栏显示"), Margin = new Thickness(0, 0, 0, 5), ToolTip = L.T("仅增加副屏显示；主屏仍沿用宿主任务栏开关。") };
                _allDisplays.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
                _allDisplays.Checked += OnDisplayOptionChanged;
                _allDisplays.Unchecked += OnDisplayOptionChanged;
                _multiStatus = Text("", 11, "TextSecondary");
                options.Children.Add(_allDisplays); options.Children.Add(_multiStatus);
                Grid.SetRow(options, 2); grid.Children.Add(options);
            }
            var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 3); grid.Children.Add(scroll);
            var hint = Text(L.T("点击已连接且有电量的设备切换任务栏显示；滚轮每格切换。"), 11, "TextSecondary");
            hint.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(hint, 4); grid.Children.Add(hint);
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
            if (_allDisplays is not null && _multiStatus is not null)
            {
                _updatingDisplayOption = true;
                _allDisplays.IsChecked = owner._preferences.AllDisplays;
                _allDisplays.IsEnabled = owner._preferencesReady && owner._context?.CreateTaskbarSurface is not null;
                _updatingDisplayOption = false;
                _multiStatus.Text = L.T(owner._storageError ?? (!owner._preferencesReady ? "正在载入多屏设置…" :
                    !owner._taskbarEnabled ? "全局任务栏显示未开启；此选项会在启用后生效。" :
                    !owner._preferences.AllDisplays ? "仅在主显示器的任务栏显示。" :
                    owner._context?.CreateTaskbarSurface is null ? "宿主未提供多屏任务栏接口。" :
                    owner._managerError ?? (owner._surfaces.Count == 0 ? "当前未创建副屏显示；主屏沿用宿主设置。" :
                    "副屏任务栏会跟随自动隐藏状态；未完整显示时不绘制。")));
            }
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
        private void OnDisplayOptionChanged(object sender, RoutedEventArgs args)
        { if (!_updatingDisplayOption && _allDisplays is not null) _owner?.SetAllDisplays(_allDisplays.IsChecked == true); }
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
            if (_allDisplays is not null) { _allDisplays.Checked -= OnDisplayOptionChanged; _allDisplays.Unchecked -= OnDisplayOptionChanged; }
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
