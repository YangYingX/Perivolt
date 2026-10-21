using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PowerHub.Controls;
using PowerHub.Models;
using PowerHub.Native;
using PowerHub.Services;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBlock = System.Windows.Controls.TextBlock;
using TextElement = System.Windows.Documents.TextElement;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using SystemColors = System.Windows.SystemColors;

namespace PowerHub;

public sealed class TaskbarBatteryWindow : IDisposable
{
    private readonly AppSettings settings;
    private readonly Action? positionChanged;
    private readonly Func<string?, IReadOnlyList<FrameworkElement>>? createPluginPopup;
    private readonly Func<string?, int, bool>? handlePluginWheel;
    private Popup? pluginPopup;
    private DispatcherTimer? pluginCloseTimer;
    private string? selectedDeviceId;
    private readonly Border surface = new() { BorderThickness = new Thickness(1), UseLayoutRounding = true, SnapsToDevicePixels = true, ClipToBounds = true };
    private readonly Grid content = new();
    private readonly Border iconSurface = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid progress = new() { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private readonly Border track = new() { CornerRadius = new CornerRadius(1.5) };
    private readonly CardBatteryFill fill = new() { IsHitTestVisible = false };
    private readonly TextBlock name = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock battery = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid reading = new();
    private readonly System.Windows.Shapes.Path charging = new() { Data = Geometry.Parse("M5,0 L0,7 H4 L3,12 L9,4 H5 Z"), Width = 7, Height = 11, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Shapes.Path icon = new() { Width = 17, Height = 17, Stretch = Stretch.Uniform, StrokeThickness = 1.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer dragTimer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private Rect lastPlacement = Rect.Empty;
    private TaskbarNative.Snapshot? layout;
    private Rect[]? occupied;
    private DateTime lastOccupancyRead;
    private bool readingOccupancy;
    private bool placing;
    private bool disposed;
    private bool pointerDown;
    private bool dragging;
    private Point dragStart;
    private double dragStartOffset;
    private int savedOffset;
    private Brush? customNameColor;
    private (bool Light, bool HighContrast)? theme;
    private HwndSource? source;
    private IntPtr parent;
    public string? LastError { get; private set; }
    public bool IsVisible => source is { IsDisposed: false } && IsWindowVisible(source.Handle);

    public TaskbarBatteryWindow(AppSettings settings, Action? positionChanged = null,
        Func<string?, IReadOnlyList<FrameworkElement>>? createPluginPopup = null, Func<string?, int, bool>? handlePluginWheel = null)
    {
        this.settings = settings;
        this.positionChanged = positionChanged;
        this.createPluginPopup = createPluginPopup;
        this.handlePluginWheel = handlePluginWheel;
        TextElement.SetFontFamily(surface, new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"));
        TextOptions.SetTextFormattingMode(surface, TextFormattingMode.Display);
        System.Windows.Documents.Typography.SetNumeralAlignment(battery, FontNumeralAlignment.Tabular);
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var row = 0; row < 3; row++) content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        reading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        reading.ColumnDefinitions.Add(new ColumnDefinition());
        reading.Children.Add(charging); reading.Children.Add(battery); Grid.SetColumn(battery, 1);
        iconSurface.Child = icon;
        content.Children.Add(iconSurface); content.Children.Add(name); content.Children.Add(reading);
        progress.Children.Add(track); progress.Children.Add(fill); content.Children.Add(progress);
        surface.Child = content;
        surface.MouseEnter += (_, _) => { pluginCloseTimer?.Stop(); OpenPluginPopup(); };
        surface.MouseLeave += (_, _) => SchedulePopupClose();
        surface.PreviewMouseWheel += (_, e) => { if (!pointerDown && handlePluginWheel?.Invoke(selectedDeviceId, e.Delta) == true) e.Handled = true; };
        surface.MouseLeftButtonDown += OnDragStart;
        surface.MouseMove += OnDragMove;
        surface.MouseLeftButtonUp += (_, e) => { FinishDrag(); e.Handled = true; };
        surface.LostMouseCapture += (_, _) => { if ((GetAsyncKeyState(1) & 0x8000) == 0) FinishDrag(); };
        positionTimer.Tick += (_, _) => UpdatePlacement();
        // A non-activating taskbar child cannot rely on background mouse capture outside its bounds.
        dragTimer.Tick += (_, _) => { if ((GetAsyncKeyState(1) & 0x8000) != 0) UpdateDrag(); else FinishDrag(); };
    }

    public void UpdateDevices(IReadOnlyList<DeviceStatus> devices)
    {
        if (disposed) return;
        var online = devices.Where(device => device.ConnectionKnown && device.IsConnected && device.HasBattery);
        var selected = settings.TaskbarDeviceId is { } id ? online.FirstOrDefault(device => device.Id == id)
            : online.OrderByDescending(device => device.IsBatteryLow == true).ThenBy(device => device.BatteryPercent ?? 101).FirstOrDefault();
        if (selectedDeviceId != selected?.Id) ClosePluginPopup();
        selectedDeviceId = selected?.Id;
        if (!settings.TaskbarEnabled || selected is null) { Suspend(); return; }
        var appearance = new DeviceCard(selected, settings);
        name.Text = appearance.Name;
        battery.Text = selected.BatteryText;
        battery.Visibility = appearance.BatteryPercentageVisibility;
        charging.Visibility = appearance.IsCharging ? Visibility.Visible : Visibility.Collapsed;
        icon.Data = appearance.IconGeometry;
        fill.Percent = selected.BatteryPercent; fill.Fill = CardBatteryFill.CreateHorizontalBrush(appearance.BatteryBrush);
        fill.ColorOpacity = appearance.ColorOpacity; fill.FillOpacity = 1;
        fill.AnimationsEnabled = appearance.AnimationsEnabled;
        fill.IsCharging = appearance.IsCharging; fill.AnimationIntensity = appearance.AnimationIntensity;
        surface.ToolTip = $"{appearance.Name} · {selected.BatteryText}\n{L.T("拖动调整任务栏位置")}";
        customNameColor = null;
        if (settings.Devices.TryGetValue(selected.Id, out var preference) && BatteryPalette.TryParseColor(preference.NameColor, out var color))
        { var custom = new SolidColorBrush(color); custom.Freeze(); customNameColor = custom; }
        ApplyTaskbarTheme(true);
        lastPlacement = Rect.Empty;
        positionTimer.Start();
        UpdatePlacement();
    }

    public void Suspend() { positionTimer.Stop(); Detach(); layout = null; occupied = null; LastError = null; }
    public void Close() => Dispose();
    public void Dispose() { disposed = true; Suspend(); }

    public void ClosePluginPopup()
    {
        pluginCloseTimer?.Stop();
        var popup = pluginPopup;
        pluginPopup = null;
        if (popup is not null) { popup.IsOpen = false; popup.Child = null; }
        ToolTipService.SetIsEnabled(surface, true);
    }

    private void OpenPluginPopup()
    {
        if (disposed || pointerDown || pluginPopup is not null || !IsVisible || createPluginPopup is null) return;
        var views = createPluginPopup(selectedDeviceId);
        if (views.Count == 0) return;
        var stack = new StackPanel();
        foreach (var view in views) stack.Children.Add(view);
        var scroll = new ScrollViewer { Content = stack, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var popup = new Popup { Child = scroll, PlacementTarget = surface, AllowsTransparency = true, StaysOpen = true,
            Placement = layout?.Edge switch { 0 => PlacementMode.Right, 1 => PlacementMode.Bottom, 2 => PlacementMode.Left, _ => PlacementMode.Top },
            VerticalOffset = layout?.Edge switch { 1 => 6, 3 => -6, _ => 0 },
            HorizontalOffset = layout?.Edge switch { 0 => 6, 2 => -6, _ => 0 } };
        scroll.MouseEnter += (_, _) => pluginCloseTimer?.Stop();
        scroll.MouseLeave += (_, _) => SchedulePopupClose();
        scroll.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => ClosePluginPopup()));
        popup.Closed += (_, _) => { if (ReferenceEquals(pluginPopup, popup)) ClosePluginPopup(); };
        pluginPopup = popup;
        ToolTipService.SetIsEnabled(surface, false);
        popup.IsOpen = true;
    }

    private void SchedulePopupClose()
    {
        if (pluginPopup is null) return;
        if (pluginCloseTimer is null)
        {
            pluginCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            pluginCloseTimer.Tick += (_, _) =>
            {
                pluginCloseTimer.Stop();
                if (!surface.IsMouseOver && pluginPopup?.Child?.IsMouseOver != true) ClosePluginPopup();
            };
        }
        pluginCloseTimer.Start();
    }

    private void Detach()
    {
        ClosePluginPopup();
        pointerDown = dragging = false;
        dragTimer.Stop();
        surface.ReleaseMouseCapture();
        surface.Visibility = Visibility.Collapsed;
        if (source is { IsDisposed: false }) { source.RootVisual = null; source.Dispose(); }
        source = null; parent = IntPtr.Zero; lastPlacement = Rect.Empty;
    }

    private void Attach(IntPtr taskbar)
    {
        Detach();
        // Match the parent only while creating the child; never change the process DPI mode.
        var previousContext = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(taskbar));
        if (previousContext == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            source = new HwndSource(new HwndSourceParameters("Perivolt.TaskbarBattery")
            {
                ParentWindow = taskbar, WindowStyle = 0x44000000, // WS_CHILD | WS_CLIPSIBLINGS.
                ExtendedWindowStyle = 0x08000000, // NOACTIVATE; accept mouse input for dragging.
                UsesPerPixelTransparency = true, Width = 1, Height = 1, HwndSourceHook = OnMessage
            });
        }
        finally { SetThreadDpiAwarenessContext(previousContext); }
        if (GetParent(source.Handle) != taskbar)
        { Detach(); throw new InvalidOperationException(L.T("无法嵌入任务栏，未启用悬浮替代。")); }
        if (!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(source.Handle), GetWindowDpiAwarenessContext(taskbar)))
        { Detach(); throw new InvalidOperationException(L.T("任务栏 DPI 模式不兼容，未挂载电量条。")); }
        source.RootVisual = surface; parent = taskbar;
    }

    private void ApplyTaskbarTheme(bool force = false)
    {
        var light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        var current = (Light: light, HighContrast: SystemParameters.HighContrast);
        if (!force && theme == current) return;
        theme = current;
        var foreground = current.HighContrast ? SystemColors.WindowTextBrush : Solid(light ? 0xFF26303Cu : 0xFFF2F5FAu);
        battery.Foreground = icon.Stroke = foreground;
        name.Foreground = current.HighContrast ? foreground : customNameColor ?? Solid(light ? 0xFF536171u : 0xFFBFC9D6u);
        surface.Background = current.HighContrast ? SystemColors.WindowBrush : Solid(light ? 0xA8FFFFFFu : 0x682E3541u);
        surface.BorderBrush = current.HighContrast ? foreground : Solid(light ? 0x20000000u : 0x24FFFFFFu);
        iconSurface.Background = current.HighContrast ? SystemColors.WindowBrush : Solid(light ? 0x0C000000u : 0x10FFFFFFu);
        track.Background = current.HighContrast ? foreground : Solid(light ? 0x24000000u : 0x30FFFFFFu);
        charging.Fill = current.HighContrast ? foreground : Solid(light ? 0xFF16805Fu : 0xFF8FE4C5u);
        surface.CornerRadius = new CornerRadius(Environment.OSVersion.Version.Build >= 22000 ? 8 : 4);
    }

    private static SolidColorBrush Solid(uint argb)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze(); return brush;
    }

    public void UpdatePlacement()
    {
        if (placing || disposed || !positionTimer.IsEnabled) return;
        placing = true;
        try
        {
            if (!TaskbarNative.TryGetTaskbar(out var snapshot))
            { Detach(); layout = null; occupied = null; LastError = L.T("任务栏暂不可用，等待恢复。"); return; }
            if (layout != snapshot)
            {
                ClosePluginPopup();
                FinishDrag();
                layout = snapshot; occupied = null; lastPlacement = Rect.Empty;
            }
            if (parent != snapshot.Handle || source is null || source.IsDisposed || !IsWindow(source.Handle) || GetParent(source.Handle) != snapshot.Handle)
                Attach(snapshot.Handle);
            ApplyTaskbarTheme();
            if (!readingOccupancy && (occupied is null || DateTime.UtcNow - lastOccupancyRead >= TimeSpan.FromSeconds(1)))
                ReadOccupancy(snapshot);
            if (occupied is null) { HideStrip(L.T("正在识别任务栏布局…")); return; }
            if (DateTime.UtcNow - lastOccupancyRead > TimeSpan.FromSeconds(5))
            { HideStrip(L.T("无法识别任务栏空闲区域，等待重试。")); return; }
            var rectangle = ComputePlacement(snapshot.Bounds, snapshot.MonitorBounds, snapshot.Edge, settings.TaskbarLeftOffset, snapshot.DpiScale, occupied);
            if (rectangle.IsEmpty) { HideStrip(L.T("任务栏空间不足，已暂时隐藏电量条。")); return; }
            if (rectangle != lastPlacement || !IsVisible)
            {
                var vertical = snapshot.Edge is 0 or 2;
                var size = new Size(rectangle.Width / snapshot.DpiScale.DpiScaleX, rectangle.Height / snapshot.DpiScale.DpiScaleY);
                SetLayout(vertical, size);
                surface.Width = size.Width; surface.Height = size.Height;
                surface.Visibility = Visibility.Visible;
                if (!SetWindowPos(source!.Handle, IntPtr.Zero, (int)Math.Round(rectangle.X - snapshot.ClientOrigin.X), (int)Math.Round(rectangle.Y - snapshot.ClientOrigin.Y),
                    (int)Math.Round(rectangle.Width), (int)Math.Round(rectangle.Height), 0x0050))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                lastPlacement = rectangle;
            }
            LastError = null;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Detach(); LastError = error.Message; }
        finally { placing = false; }
    }

    private async void ReadOccupancy(TaskbarNative.Snapshot snapshot)
    {
        readingOccupancy = true;
        Rect[]? result = null;
        try { result = await Task.Run(() => TaskbarNative.ReadOccupiedBounds(snapshot.Handle)); }
        catch (Exception error) when (error is InvalidOperationException or COMException or UnauthorizedAccessException) { }
        finally { readingOccupancy = false; }
        if (disposed || !positionTimer.IsEnabled || layout != snapshot) return;
        occupied = result;
        lastOccupancyRead = DateTime.UtcNow;
        if (result is null) { HideStrip(L.T("无法识别任务栏空闲区域，等待重试。")); return; }
        UpdatePlacement();
    }

    private void HideStrip(string reason)
    {
        ClosePluginPopup();
        FinishDrag();
        surface.Visibility = Visibility.Collapsed;
        if (source is { IsDisposed: false }) ShowWindow(source.Handle, 0);
        LastError = reason;
    }

    private void SetLayout(bool vertical, Size size)
    {
        var compact = !vertical && (size.Width < 156 || size.Height < 30);
        var small = vertical ? size.Height < 68 : size.Height < 36;
        surface.Cursor = vertical ? System.Windows.Input.Cursors.SizeNS : System.Windows.Input.Cursors.SizeWE;
        surface.Padding = vertical ? new Thickness(5, small ? 3 : 6, 5, small ? 3 : 6) : new Thickness(7, small ? 2 : 4, 7, small ? 2 : 4);
        content.VerticalAlignment = VerticalAlignment.Center;
        content.ColumnDefinitions[0].Width = vertical ? new GridLength(0) : GridLength.Auto;
        content.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        content.ColumnDefinitions[2].Width = vertical ? new GridLength(0) : GridLength.Auto;
        iconSurface.Width = iconSurface.Height = Math.Min(small ? 20 : 26, vertical ? size.Width - 12 : size.Height - (small ? 6 : 10));
        icon.Width = icon.Height = Math.Min(small ? 15 : 17, iconSurface.Width - 2);
        iconSurface.Margin = vertical ? new Thickness(0, 0, 0, 3) : new Thickness(0, 0, 8, 0);
        iconSurface.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        name.Visibility = vertical || compact ? Visibility.Collapsed : Visibility.Visible;
        reading.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        reading.MaxWidth = vertical ? Math.Max(0, size.Width - 14) : compact ? Math.Max(0, size.Width - 48) : 78;
        battery.FontSize = vertical ? 11 : 12;
        progress.Visibility = fill.Percent.HasValue && (vertical || size.Height >= 28) ? Visibility.Visible : Visibility.Collapsed;
        progress.Height = small ? 2 : 3;
        progress.Margin = new Thickness(0, vertical ? 5 : 3, 0, 0);
        Grid.SetColumn(iconSurface, vertical ? 1 : 0); Grid.SetRow(iconSurface, 0); Grid.SetRowSpan(iconSurface, vertical ? 1 : 2);
        Grid.SetColumn(name, 1); Grid.SetRow(name, 0);
        Grid.SetColumn(reading, vertical ? 1 : compact ? 1 : 2); Grid.SetRow(reading, vertical ? 1 : 0);
        Grid.SetColumnSpan(reading, !vertical && compact ? 2 : 1);
        Grid.SetColumn(progress, 1); Grid.SetRow(progress, vertical ? 2 : 1); Grid.SetColumnSpan(progress, vertical ? 1 : 2);
    }

    internal static Rect ComputePlacement(Rect taskbar, Rect screen, uint edge, double offset, DpiScale dpi, IReadOnlyList<Rect>? occupied = null)
    {
        var visible = Rect.Intersect(taskbar, screen);
        if (visible.IsEmpty || dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0) return Rect.Empty;
        var vertical = edge is 0 or 2;
        var scale = vertical ? dpi.DpiScaleY : dpi.DpiScaleX;
        var crossScale = vertical ? dpi.DpiScaleX : dpi.DpiScaleY;
        var crossSize = vertical ? visible.Width : visible.Height;
        if (crossSize < (vertical ? 48 : 32) * crossScale) return Rect.Empty;
        var start = (vertical ? visible.Top : visible.Left) + 4 * scale;
        var end = (vertical ? visible.Bottom : visible.Right) - 4 * scale;
        var preferred = (vertical ? taskbar.Top : taskbar.Left) + (double.IsFinite(offset) ? Math.Max(0, offset) : 0) * scale;
        var minimum = (vertical ? 54 : 96) * scale;
        var desired = (vertical ? 76 : visible.Height < 36 * crossScale ? 112 : 200) * scale;
        var spans = (occupied ?? []).Select(bounds => Rect.Intersect(bounds, visible)).Where(bounds => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            .Select(bounds => (Start: (vertical ? bounds.Top : bounds.Left) - 5 * scale, End: (vertical ? bounds.Bottom : bounds.Right) + 5 * scale))
            .Where(span => span.End > start && span.Start < end).OrderBy(span => span.Start).ToArray();
        var best = double.PositiveInfinity;
        var bestStart = 0d;
        var bestSize = 0d;
        void Consider(double from, double to)
        {
            if (to - from < minimum) return;
            var length = Math.Min(desired, to - from);
            var position = Math.Clamp(preferred, from, to - length);
            var distance = Math.Abs(position - preferred);
            if (distance >= best) return;
            best = distance; bestStart = position; bestSize = length;
        }
        var cursor = start;
        foreach (var span in spans) { Consider(cursor, Math.Min(span.Start, end)); cursor = Math.Max(cursor, span.End); }
        Consider(cursor, end);
        if (bestSize == 0) return Rect.Empty;
        var thickness = Math.Min((vertical ? 104 : 40) * crossScale, crossSize - 8 * crossScale);
        var across = (vertical ? visible.Left : visible.Top) + (crossSize - thickness) / 2;
        return vertical ? new Rect(across, bestStart, thickness, bestSize) : new Rect(bestStart, across, bestSize, thickness);
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (layout is not { } snapshot || lastPlacement.IsEmpty || !GetPhysicalCursorPos(out var cursor)) return;
        ClosePluginPopup();
        pointerDown = true;
        surface.CaptureMouse();
        dragging = false;
        savedOffset = settings.TaskbarLeftOffset;
        dragStart = new Point(cursor.X, cursor.Y);
        dragStartOffset = snapshot.Edge is 0 or 2 ? (lastPlacement.Top - snapshot.Bounds.Top) / snapshot.DpiScale.DpiScaleY
            : (lastPlacement.Left - snapshot.Bounds.Left) / snapshot.DpiScale.DpiScaleX;
        dragTimer.Start();
        e.Handled = true;
    }

    private void OnDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!pointerDown) return;
        if (e.LeftButton != MouseButtonState.Pressed) { FinishDrag(); return; }
        UpdateDrag();
        e.Handled = true;
    }

    private void UpdateDrag()
    {
        if (!pointerDown || layout is not { } snapshot || !GetPhysicalCursorPos(out var cursor)) return;
        var vertical = snapshot.Edge is 0 or 2;
        var delta = vertical ? (cursor.Y - dragStart.Y) / snapshot.DpiScale.DpiScaleY : (cursor.X - dragStart.X) / snapshot.DpiScale.DpiScaleX;
        if (!dragging && Math.Abs(delta) < (vertical ? SystemParameters.MinimumVerticalDragDistance : SystemParameters.MinimumHorizontalDragDistance)) return;
        dragging = true;
        var offset = (int)Math.Clamp(Math.Round(dragStartOffset + delta), 0, 32767);
        if (settings.TaskbarLeftOffset == offset) return;
        settings.TaskbarLeftOffset = offset;
        UpdatePlacement();
    }

    private void FinishDrag()
    {
        if (!pointerDown) return;
        var changed = dragging;
        pointerDown = dragging = false;
        dragTimer.Stop();
        surface.ReleaseMouseCapture();
        if (!changed) return;
        if (layout is { } snapshot && !lastPlacement.IsEmpty)
        {
            var offset = snapshot.Edge is 0 or 2 ? (lastPlacement.Top - snapshot.Bounds.Top) / snapshot.DpiScale.DpiScaleY
                : (lastPlacement.Left - snapshot.Bounds.Left) / snapshot.DpiScale.DpiScaleX;
            settings.TaskbarLeftOffset = (int)Math.Clamp(Math.Round(offset), 0, 32767);
        }
        if (settings.TaskbarLeftOffset != savedOffset) positionChanged?.Invoke();
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE.
        if (message == 0x020A && !pointerDown && handlePluginWheel?.Invoke(selectedDeviceId, (short)(wParam.ToInt64() >> 16)) == true)
        { handled = true; return IntPtr.Zero; }
        if (message is 0x007E or 0x02E0 or 0x001A or 0x031A)
        {
            theme = null; lastPlacement = Rect.Empty; occupied = null;
            surface.Dispatcher.BeginInvoke(new Action(UpdatePlacement));
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
