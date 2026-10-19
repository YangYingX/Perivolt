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
    private readonly AppSettings _settings;
    private readonly Action _settingsChanged;
    private readonly Action _openDashboard;
    private readonly DispatcherTimer _positionSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _applyingSettings;
    private bool _closingForExit;
    private bool _positionInitialized;
    private bool _dragging;

    public DesktopWidgetWindow(AppSettings settings, Action settingsChanged, Action openDashboard)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _openDashboard = openDashboard;
        InitializeComponent();
        _positionSaveTimer.Tick += (_, _) => SavePosition();
        Loaded += (_, _) => ApplySettings();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(new Action(EnsureVisiblePosition)); };
        LocationChanged += (_, _) =>
        {
            if (_applyingSettings || _dragging || !IsLoaded || !IsVisible || _closingForExit) return;
            _positionSaveTimer.Stop();
            _positionSaveTimer.Start();
        };
        SizeChanged += (_, _) => { if (IsLoaded && !_applyingSettings) EnsureVisiblePosition(); };
        Closing += OnClosing;
        ApplySettings();
    }

    public void UpdateDevices(IReadOnlyList<DeviceStatus> devices)
    {
        var pinned = devices.Where(device => device.ConnectionKnown && device.IsConnected && _settings.IsPinned(device.Id))
            .OrderBy(device => _settings.GetDeviceOrder(device.Id));
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
                needsCharge, needsCharge && percent is <= 10, appearance.DisplayStyle,
                appearance.AnimationsEnabled, appearance.ShowBatteryPercentage, appearance.FillOpacity, appearance.AnimationIntensity, appearance.ColorOpacity,
                _settings.WidgetCompact, _settings.WidgetTransparent,
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
            DragHeader.Cursor = _settings.WidgetLocked ? Cursors.Arrow : Cursors.SizeAll;
            OpenDashboardButton.IsEnabled = HideWidgetButton.IsEnabled = !_settings.WidgetLocked;
            TopmostMenuItem.IsChecked = _settings.WidgetTopmost;
            LockedMenuItem.IsChecked = _settings.WidgetLocked;
            FooterText.Text = $"PowerHub · {(_settings.NotificationsEnabled ? "充电提醒已开启" : "充电提醒已关闭")}";
            if (IsLoaded && !_positionInitialized)
            {
                _positionInitialized = true;
                if (_settings.WidgetLeft is double left && double.IsFinite(left) &&
                    _settings.WidgetTop is double top && double.IsFinite(top))
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
        }
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
        EnsureVisiblePosition();
        SavePosition();
    }

    public void ResetPosition()
    {
        if (!IsLoaded || _dragging || _closingForExit) return;
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        if (primary is null) return;
        var area = primary.WorkingArea;
        // Move onto the primary display first so WPF applies that display's DPI before choosing the margin.
        MoveToPixels(new Point(area.Left, area.Top));
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closingForExit || _dragging || !IsVisible || !TryGetBounds(out var bounds)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            MoveToPixels(new Point(Math.Max(area.Left, area.Right - bounds.Width - 20 * dpi.DpiScaleX),
                Math.Min(area.Top + 20 * dpi.DpiScaleY, Math.Max(area.Top, area.Bottom - bounds.Height))));
            SavePosition();
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
            MoveToPixels(position);
            SavePosition();
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

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
        string DisplayStyle, bool AnimationsEnabled, bool ShowBatteryPercentage, double FillOpacity, double AnimationIntensity, double ColorOpacity,
        bool Compact, bool Transparent,
        System.Windows.Media.Brush NameBrush, System.Windows.Media.Brush BatteryBrush, bool IsCharging,
        System.Windows.Media.Geometry IconGeometry)
    {
        public Visibility BatteryPercentageVisibility => Percent.HasValue && !ShowBatteryPercentage ? Visibility.Collapsed : Visibility.Visible;
        private bool LabelBesideVisual => DisplayStyle == "Hourglass" && Percent.HasValue && ShowBatteryPercentage;
        public double BatteryVisualSize => Compact ? 28 : 48;
        public double BatteryAreaWidth => BatteryVisualSize + (LabelBesideVisual ? 30 : 0);
        public System.Windows.HorizontalAlignment BatteryVisualAlignment => LabelBesideVisual
            ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Center;
        public System.Windows.HorizontalAlignment BatteryLabelAlignment => LabelBesideVisual
            ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Center;
    }
}
