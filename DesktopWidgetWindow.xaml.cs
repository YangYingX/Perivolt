using PowerHub.Services;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PowerHub.Models;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace PowerHub;

public partial class DesktopWidgetWindow : Window
{
    private static readonly Geometry LockedIcon = (Geometry)Geometry.Parse("M2,6 H10 V13 H2 Z M3,6 V4 A3,3 0 0 1 9,4 V6 M6,9 V11").GetAsFrozen();
    private static readonly Geometry UnlockedIcon = (Geometry)Geometry.Parse("M2,6 H10 V13 H2 Z M3,6 V4 A3,3 0 0 1 9,4 M6,9 V11").GetAsFrozen();
    private readonly AppSettings _settings;
    private readonly Action _settingsChanged;
    private readonly Action _openDashboard;
    private readonly string? _deviceId;
    private readonly Func<IReadOnlyList<DesktopWidgetWindow>>? _snapPeers;
    private int _placementIndex;
    private int _positionRevision;
    private readonly DispatcherTimer _positionSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _applyingSettings;
    private bool _closingForExit;
    private bool _positionInitialized;
    private bool _dragging;
    private WidgetUnlockWindow? _unlockWindow;

    public DesktopWidgetWindow(AppSettings settings, Action settingsChanged, Action openDashboard)
        : this(settings, settingsChanged, openDashboard, null, null, 0) { }

    public DesktopWidgetWindow(AppSettings settings, Action settingsChanged, Action openDashboard,
        string? deviceId, Func<IReadOnlyList<DesktopWidgetWindow>>? snapPeers, int placementIndex)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _openDashboard = openDashboard;
        _deviceId = deviceId;
        _snapPeers = snapPeers;
        _placementIndex = placementIndex;
        InitializeComponent();
        _positionSaveTimer.Tick += (_, _) => SavePosition();
        Loaded += (_, _) => ApplySettings();
        SourceInitialized += (_, _) => { HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage); PowerHub.Native.OverlayNative.SetMousePassthrough(this, _settings.WidgetLocked); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(EnsureVisiblePosition)); SyncUnlockButton(); };
        LocationChanged += (_, _) =>
        {
            SyncUnlockButton();
            if (_applyingSettings || _dragging || !IsLoaded || !IsVisible || _closingForExit) return;
            _positionSaveTimer.Stop();
            _positionSaveTimer.Start();
        };
        SizeChanged += (_, _) => { if (IsLoaded && !_applyingSettings) EnsureVisiblePosition(); SyncUnlockButton(); };
        Closing += OnClosing;
        ApplySettings();
    }

    public void UpdateDevices(IReadOnlyList<DeviceStatus> devices)
    {
        var pinned = devices.Where(device => device.ConnectionKnown && device.IsConnected && _settings.IsPinned(device.Id) &&
                (_deviceId is null || string.Equals(_settings.ResolveDeviceId(device.Id), _deviceId, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(device => _settings.GetDeviceOrder(device.Id));
        var items = pinned.Select(device =>
        {
            var appearance = new DeviceCard(device, _settings);
            var name = appearance.Name;
            var percent = device.BatteryPercent is int value ? Math.Clamp(value, 0, 100) : (int?)null;
            var needsCharge = device.IsCharging != true && (percent is int remaining ? remaining <= _settings.GetThreshold(device.Id) : device.IsBatteryLow == true);
            var status = device.IsCharging == true ? L.T("正在充电") : _settings.WidgetCompact
                ? needsCharge ? L.T("电量偏低") : "" : L.F("已连接 · {0}", L.T(device.Connection));
            var label = percent is int battery ? $"{battery}%" : _settings.WidgetCompact && device.BatteryLevel is not null ? device.BatteryText : "—";
            var advice = _settings.WidgetCompact ? "" : needsCharge ? L.T("该充电了") : !percent.HasValue && device.BatteryLevel is not null
                ? L.F("电量：{0}", device.BatteryText) : !device.HasBattery ? L.T("暂未获取电量") : "";
            return new WidgetDevice(name, status, advice, percent, label,
                percent.HasValue ? $"{name}，{label}，{status}" : L.F("{0}，电量未知，{1}", name, status),
                needsCharge, needsCharge && percent is <= 10, appearance.DisplayStyle,
                appearance.AnimationsEnabled, appearance.ShowBatteryPercentage, appearance.FillOpacity, appearance.AnimationIntensity, appearance.ColorOpacity,
                _settings.WidgetCompact, _settings.WidgetTransparent,
                appearance.NameBrush, appearance.BatteryBrush, device.IsCharging == true, appearance.IconGeometry);
        }).ToList();
        DeviceItems.ItemsSource = items;
        WidgetTitleText.Text = _deviceId is null ? L.T("桌面电量") : items.FirstOrDefault()?.Name ?? L.T("桌面电量");
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var lowCount = items.Count(item => item.NeedsCharge);
        SummaryText.Text = items.Count == 0 ? L.T("连接固定的外设后显示电量") : lowCount > 0
            ? L.F("{0} 个设备 · {1} 个需要充电", items.Count, lowCount) : L.F("{0} 个常用设备", items.Count);
    }

    public void ApplySettings()
    {
        _applyingSettings = true;
        try
        {
            Width = _settings.WidgetCompact ? 260 : 312;
            WidgetSurface.Padding = _settings.WidgetCompact ? new Thickness(12, 6, 12, 4) : new Thickness(16, 14, 16, 12);
            DragHeader.Margin = new Thickness(0, 0, 0, _settings.WidgetCompact ? 4 : 12);
            SummaryText.Visibility = _settings.WidgetCompact ? Visibility.Collapsed : Visibility.Visible;
            FooterText.Visibility = _settings.WidgetCompact ? Visibility.Collapsed : Visibility.Visible;
            EmptyExplanation.Visibility = _settings.WidgetCompact ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Padding = _settings.WidgetCompact ? new Thickness(12, 14, 12, 10) : new Thickness(16, 20, 16, 20);
            WidgetShadow.Opacity = _settings.WidgetTransparent ? .07 : .14;
            if (_settings.WidgetTransparent)
            {
                Resources["WidgetRowBackdrop"] = TranslucentBrush("SurfaceAlt", .4);
                WidgetSurface.Background = TranslucentBrush("Surface", .78);
                EmptyState.Background = (System.Windows.Media.Brush)Resources["WidgetRowBackdrop"];
            }
            else
            {
                WidgetSurface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Surface");
                EmptyState.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SurfaceAlt");
            }
            EmptyState.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Stroke");
            EmptyState.BorderThickness = new Thickness(_settings.WidgetTransparent ? 1 : 0);
            Topmost = _settings.WidgetTopmost;
            PowerHub.Native.OverlayNative.SetMousePassthrough(this, _settings.WidgetLocked);
            DragHeader.Cursor = _settings.WidgetLocked ? Cursors.Arrow : Cursors.SizeAll;
            OpenDashboardButton.IsEnabled = HideWidgetButton.IsEnabled = !_settings.WidgetLocked;
            LockStateIcon.Data = _settings.WidgetLocked ? LockedIcon : UnlockedIcon;
            ToggleLockButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, _settings.WidgetLocked ? "Accent" : "TextSecondary");
            ToggleLockButton.ToolTip = _settings.WidgetLocked ? L.T("解锁位置") : L.T("锁定位置");
            System.Windows.Automation.AutomationProperties.SetName(ToggleLockButton, (string)ToggleLockButton.ToolTip);
            TopmostMenuItem.IsChecked = _settings.WidgetTopmost;
            LockedMenuItem.IsChecked = _settings.WidgetLocked;
            FooterText.Text = L.F("PowerHub · {0}", (_settings.NotificationsEnabled ? L.T("充电提醒已开启") : L.T("充电提醒已关闭")));
            if (IsLoaded && !_positionInitialized)
            {
                _positionInitialized = true;
                var saved = _deviceId is null ? null : _settings.WidgetPositions.GetValueOrDefault(_deviceId);
                var savedLeft = _deviceId is null ? _settings.WidgetLeft : saved?.Left;
                var savedTop = _deviceId is null ? _settings.WidgetTop : saved?.Top;
                if (savedLeft is double left && double.IsFinite(left) &&
                    savedTop is double top && double.IsFinite(top))
                {
                    Left = left;
                    Top = top;
                    Dispatcher.BeginInvoke(new Action(EnsureVisiblePosition));
                }
                else ResetPosition();
            }
        }
        finally
        {
            _applyingSettings = false;
            Dispatcher.BeginInvoke(new Action(SyncUnlockButton), DispatcherPriority.Loaded);
        }
    }

    private void SyncUnlockButton()
    {
        if (_closingForExit || !IsLoaded || !IsVisible || !_settings.WidgetLocked || ToggleLockButton.ActualWidth <= 0)
        { _unlockWindow?.Hide(); return; }
        _unlockWindow ??= new WidgetUnlockWindow(this, () => { _settings.WidgetLocked = false; _settingsChanged(); });
        _unlockWindow.RefreshText();
        _unlockWindow.Topmost = _settings.WidgetTopmost;
        if (!_unlockWindow.IsVisible) _unlockWindow.Show();
        var point = ToggleLockButton.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(this);
        PowerHub.Native.OverlayNative.MovePixels(_unlockWindow, (int)Math.Round(point.X), (int)Math.Round(point.Y),
            (int)Math.Round(ToggleLockButton.ActualWidth * dpi.DpiScaleX), (int)Math.Round(ToggleLockButton.ActualHeight * dpi.DpiScaleY));
    }

    private System.Windows.Media.Brush TranslucentBrush(string resource, double opacity)
    {
        var source = TryFindResource(resource) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
        var brush = source.CloneCurrentValue();
        brush.Opacity *= opacity;
        brush.Freeze();
        return brush;
    }

    public void CloseForExit()
    {
        _closingForExit = true;
        _unlockWindow?.Close();
        _positionSaveTimer.Stop();
        Close();
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.WidgetLocked || e.ButtonState != MouseButtonState.Pressed) return;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != DragHeader; source = VisualTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        _positionSaveTimer.Stop();
        _dragging = true;
        try { DragMove(); }
        finally { _dragging = false; }
        SnapPosition();
        EnsureVisiblePosition();
        SavePosition();
    }

    public void RestorePosition()
    {
        _positionSaveTimer.Stop();
        _positionRevision++;
        _positionInitialized = false;
        ApplySettings();
    }

    public void FlushPosition() => SavePosition(false);

    public void ResetPosition() => ResetPosition(_placementIndex);

    public void ResetPosition(int placementIndex)
    {
        if (!IsLoaded || _dragging || _closingForExit) return;
        _placementIndex = placementIndex;
        var revision = ++_positionRevision;
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        if (primary is null) return;
        var area = primary.WorkingArea;
        // Move onto the primary display first so WPF applies that display's DPI before choosing the margin.
        MoveWithoutSaving(new Point(area.Left, area.Top));
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (revision != _positionRevision || _closingForExit || _dragging || !IsVisible || !TryGetBounds(out var bounds)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var gap = 12 * dpi.DpiScaleY;
            var rows = Math.Max(1, (int)((area.Height - 40 * dpi.DpiScaleY + gap) / (bounds.Height + gap)));
            var x = area.Right - bounds.Width - 20 * dpi.DpiScaleX - (_placementIndex / rows) * (bounds.Width + 12 * dpi.DpiScaleX);
            var y = area.Top + 20 * dpi.DpiScaleY + (_placementIndex % rows) * (bounds.Height + gap);
            MoveWithoutSaving(new Point(Math.Max(area.Left, x), Math.Min(y, Math.Max(area.Top, area.Bottom - bounds.Height))));
            SavePosition(false);
        }), DispatcherPriority.Loaded);
    }

    private void EnsureVisiblePosition()
    {
        if (!IsLoaded || !IsVisible || _dragging || _closingForExit || DragHeader.ActualWidth <= 0 || !TryGetBounds(out var bounds)) return;
        var header = new Rect(DragHeader.PointToScreen(new Point()),
            DragHeader.PointToScreen(new Point(DragHeader.ActualWidth, DragHeader.ActualHeight)));
        var areas = System.Windows.Forms.Screen.AllScreens.Select(screen => new Rect(
            screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height)).ToArray();
        if (FindRecoveryPosition(bounds, header, areas, VisualTreeHelper.GetDpi(this)) is Point position)
        {
            MoveWithoutSaving(position);
            SavePosition(false);
        }
    }

    private void SnapPosition()
    {
        if (!_settings.WidgetSnapEnabled || !TryGetBounds(out var bounds)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var areas = System.Windows.Forms.Screen.AllScreens.Select(screen => new Rect(screen.WorkingArea.Left,
            screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height)).ToArray();
        var peers = (_snapPeers?.Invoke() ?? []).Where(window => window != this && window.IsVisible)
            .Select(window => window.TryGetBounds(out var rectangle) ? rectangle : Rect.Empty).Where(rectangle => !rectangle.IsEmpty).ToArray();
        var position = FindSnapPosition(bounds, areas, peers, dpi);
        MoveWithoutSaving(position);
    }

    internal static Point FindSnapPosition(Rect bounds, IReadOnlyList<Rect> areas, IReadOnlyList<Rect> peers, DpiScale dpi)
    {
        var thresholdX = 16 * dpi.DpiScaleX;
        var thresholdY = 16 * dpi.DpiScaleY;
        var gapX = 12 * dpi.DpiScaleX;
        var gapY = 12 * dpi.DpiScaleY;
        var deltaX = double.PositiveInfinity;
        var deltaY = double.PositiveInfinity;
        void ConsiderX(double x) { var delta = x - bounds.Left; if (Math.Abs(delta) <= thresholdX && Math.Abs(delta) < Math.Abs(deltaX)) deltaX = delta; }
        void ConsiderY(double y) { var delta = y - bounds.Top; if (Math.Abs(delta) <= thresholdY && Math.Abs(delta) < Math.Abs(deltaY)) deltaY = delta; }
        foreach (var area in areas.Where(area => area.IntersectsWith(bounds)))
        {
            ConsiderX(area.Left + gapX); ConsiderX(area.Right - gapX - bounds.Width);
            ConsiderY(area.Top + gapY); ConsiderY(area.Bottom - gapY - bounds.Height);
        }
        foreach (var peer in peers)
        {
            if (bounds.Top <= peer.Bottom + gapY + thresholdY && bounds.Bottom >= peer.Top - gapY - thresholdY)
            {
                ConsiderX(peer.Left); ConsiderX(peer.Right - bounds.Width);
                ConsiderX(peer.Left - gapX - bounds.Width); ConsiderX(peer.Right + gapX);
            }
            if (bounds.Left <= peer.Right + gapX + thresholdX && bounds.Right >= peer.Left - gapX - thresholdX)
            {
                ConsiderY(peer.Top); ConsiderY(peer.Bottom - bounds.Height);
                ConsiderY(peer.Top - gapY - bounds.Height); ConsiderY(peer.Bottom + gapY);
            }
        }
        return new Point(bounds.Left + (double.IsFinite(deltaX) ? deltaX : 0), bounds.Top + (double.IsFinite(deltaY) ? deltaY : 0));
    }

    internal static Point? FindRecoveryPosition(Rect bounds, Rect header, IReadOnlyList<Rect> areas, DpiScale dpi)
    {
        // Compare everything in physical pixels. A usable drag region is sufficient; intentional partial placement stays put.
        if (areas.Count == 0 || areas.Any(area =>
        {
            var visible = Rect.Intersect(header, area);
            return visible.Width >= Math.Min(header.Width, 100 * dpi.DpiScaleX) &&
                visible.Height >= Math.Min(header.Height, 24 * dpi.DpiScaleY);
        })) return null;
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var target = areas.OrderBy(area =>
        {
            var x = Math.Clamp(center.X, area.Left, area.Right) - center.X;
            var y = Math.Clamp(center.Y, area.Top, area.Bottom) - center.Y;
            return x * x + y * y;
        }).First();
        return new Point(Math.Clamp(bounds.Left, target.Left, Math.Max(target.Left, target.Right - bounds.Width)),
            Math.Clamp(bounds.Top, target.Top, Math.Max(target.Top, target.Bottom - bounds.Height)));
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Display/DPI/work-area notifications for this window only; no global hooks or polling.
        if (message is 0x007E or 0x02E0 || (message == 0x001A && wParam.ToInt64() == 0x002F))
            Dispatcher.BeginInvoke(new Action(EnsureVisiblePosition), DispatcherPriority.Loaded);
        return IntPtr.Zero;
    }

    private bool TryGetBounds(out Rect bounds)
    {
        var found = GetWindowRect(new WindowInteropHelper(this).Handle, out var rectangle);
        bounds = found ? new Rect(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top) : Rect.Empty;
        return found;
    }

    private void MoveToPixels(Point position) => SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
        (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE

    private void MoveWithoutSaving(Point position)
    {
        var applying = _applyingSettings;
        _applyingSettings = true;
        _positionSaveTimer.Stop();
        try { MoveToPixels(position); }
        finally { _applyingSettings = applying; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private void SavePosition(bool notify = true)
    {
        _positionSaveTimer.Stop();
        if (!IsLoaded || _closingForExit) return;
        if (_deviceId is null)
        {
            _settings.WidgetLeft = Left;
            _settings.WidgetTop = Top;
        }
        else _settings.WidgetPositions[_deviceId] = new() { Left = Left, Top = Top };
        if (notify) _settingsChanged();
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

    private void OnToggleLock(object sender, RoutedEventArgs e)
    {
        _settings.WidgetLocked = !_settings.WidgetLocked;
        _settingsChanged();
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e) => _openDashboard();

    private void OnHideWidget(object sender, RoutedEventArgs e) => HideWidget();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingForExit) return;
        e.Cancel = true;
        // Let the canceled close finish before the manager removes this independent window.
        Dispatcher.BeginInvoke(new Action(HideWidget), DispatcherPriority.ContextIdle);
    }

    private void HideWidget()
    {
        if (_closingForExit) return;
        SavePosition(false);
        if (_deviceId is null) _settings.WidgetEnabled = false;
        else if (_settings.Devices.TryGetValue(_deviceId, out var preference)) preference.Pinned = false;
        Hide();
        _settingsChanged();
    }

    private sealed record WidgetDevice(string Name, string Status, string Advice, int? Percent,
        string BatteryLabel, string BatteryDescription, bool NeedsCharge, bool IsCritical,
        string DisplayStyle, bool AnimationsEnabled, bool ShowBatteryPercentage, double FillOpacity, double AnimationIntensity, double ColorOpacity,
        bool Compact, bool Transparent,
        System.Windows.Media.Brush NameBrush, System.Windows.Media.Brush BatteryBrush, bool IsCharging,
        System.Windows.Media.Geometry IconGeometry)
    {
        public Visibility BatteryPercentageVisibility => Percent.HasValue && !ShowBatteryPercentage ? Visibility.Collapsed : Visibility.Visible;
        public bool IsFillStyle => DisplayStyle == "Fill";
        public Visibility CardFillVisibility => IsFillStyle ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BatteryGraphicVisibility => IsFillStyle ? Visibility.Collapsed : Visibility.Visible;
        public double BatteryVisualSize => Compact ? 28 : 48;
    }
}
