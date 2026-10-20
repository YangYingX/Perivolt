using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PowerHub.Controls;
using PowerHub.Models;
using PowerHub.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using TextBlock = System.Windows.Controls.TextBlock;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextElement = System.Windows.Documents.TextElement;

namespace PowerHub;

public sealed class TaskbarBatteryWindow : IDisposable
{
    private readonly AppSettings settings;
    private readonly Border surface = new() { CornerRadius = new CornerRadius(6), Margin = new Thickness(4), Padding = new Thickness(8, 3, 8, 3) };
    private readonly Grid content = new();
    private readonly Grid progress = new() { Height = 3, Margin = new Thickness(0, 4, 10, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly Border track = new() { CornerRadius = new CornerRadius(1.5) };
    private readonly CardBatteryFill fill = new() { IsHitTestVisible = false };
    private readonly TextBlock name = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock battery = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly System.Windows.Shapes.Path icon = new() { Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Rect lastPlacement = Rect.Empty;
    private bool placing;
    private Brush? customNameColor;
    private HwndSource? source;
    private IntPtr parent;
    public string? LastError { get; private set; }
    public bool IsVisible => source is { IsDisposed: false } && IsWindowVisible(source.Handle);

    public TaskbarBatteryWindow(AppSettings settings)
    {
        this.settings = settings;
        surface.UseLayoutRounding = true; surface.SnapsToDevicePixels = true;
        TextElement.SetFontFamily(surface, new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"));
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(icon); content.Children.Add(name); content.Children.Add(battery);
        progress.Children.Add(track); progress.Children.Add(fill); content.Children.Add(progress);
        surface.Child = content;
        positionTimer.Tick += (_, _) => UpdatePlacement();
    }

    public void UpdateDevices(IReadOnlyList<DeviceStatus> devices)
    {
        var online = devices.Where(device => device.ConnectionKnown && device.IsConnected && device.HasBattery);
        var selected = settings.TaskbarDeviceId is { } id ? online.FirstOrDefault(device => device.Id == id)
            : online.OrderByDescending(device => device.IsBatteryLow == true).ThenBy(device => device.BatteryPercent ?? 101).FirstOrDefault();
        if (!settings.TaskbarEnabled || selected is null) { Suspend(); return; }
        var appearance = new DeviceCard(selected, settings);
        name.Text = appearance.Name;
        battery.Text = selected.BatteryText;
        battery.Visibility = appearance.BatteryPercentageVisibility;
        icon.Data = appearance.IconGeometry;
        fill.Percent = selected.BatteryPercent; fill.Fill = appearance.BatteryBrush;
        if (fill.Fill is LinearGradientBrush gradient)
        {
            var horizontal = gradient.Clone(); horizontal.StartPoint = new System.Windows.Point(0, 0); horizontal.EndPoint = new System.Windows.Point(1, 0);
            horizontal.Freeze(); fill.Fill = horizontal;
        }
        fill.ColorOpacity = appearance.ColorOpacity; fill.FillOpacity = 1;
        fill.AnimationsEnabled = appearance.AnimationsEnabled;
        customNameColor = null;
        if (settings.Devices.TryGetValue(selected.Id, out var preference) && BatteryPalette.TryParseColor(preference.NameColor, out var color))
        { var custom = new SolidColorBrush(color); custom.Freeze(); customNameColor = custom; }
        ApplyTaskbarTheme();
        positionTimer.Start();
        UpdatePlacement();
    }

    public void Suspend() { positionTimer.Stop(); Detach(); LastError = null; }
    public void Close() => Dispose();
    public void Dispose() { positionTimer.Stop(); Detach(); }

    private void Detach()
    {
        if (source is { IsDisposed: false }) { source.RootVisual = null; source.Dispose(); }
        source = null; parent = IntPtr.Zero; lastPlacement = Rect.Empty;
    }

    private void Attach(IntPtr taskbar)
    {
        Detach();
        if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), GetWindowDpiAwarenessContext(taskbar)))
            throw new InvalidOperationException(L.T("任务栏 DPI 模式不兼容，未挂载电量条。"));
        source = new HwndSource(new HwndSourceParameters("Perivolt.TaskbarBattery")
        {
            ParentWindow = taskbar, WindowStyle = 0x44000000, // WS_CHILD | WS_CLIPSIBLINGS; never a desktop overlay.
            ExtendedWindowStyle = 0x08000020, // NOACTIVATE | TRANSPARENT; input stays with the taskbar.
            UsesPerPixelTransparency = true, Width = 1, Height = 1, HwndSourceHook = OnMessage
        });
        if (GetParent(source.Handle) != taskbar)
        { Detach(); throw new InvalidOperationException(L.T("无法嵌入任务栏，未启用悬浮替代。")); }
        source.RootVisual = surface; parent = taskbar;
    }

    private void ApplyTaskbarTheme()
    {
        var light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        var foreground = new SolidColorBrush(light ? System.Windows.Media.Color.FromRgb(30, 38, 48) : System.Windows.Media.Color.FromRgb(239, 243, 249));
        foreground.Freeze(); battery.Foreground = icon.Stroke = foreground; name.Foreground = customNameColor ?? foreground;
        surface.Background = new SolidColorBrush(light ? System.Windows.Media.Color.FromArgb(12, 0, 0, 0) : System.Windows.Media.Color.FromArgb(18, 255, 255, 255));
        track.Background = new SolidColorBrush(light ? System.Windows.Media.Color.FromArgb(32, 0, 0, 0) : System.Windows.Media.Color.FromArgb(48, 255, 255, 255));
    }

    public void UpdatePlacement()
    {
        if (placing || !positionTimer.IsEnabled) return;
        placing = true;
        try
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !GetClientRect(taskbar, out var bar)) { Detach(); LastError = L.T("任务栏暂不可用，等待恢复。"); return; }
            if (parent != taskbar || source is null || source.IsDisposed || !IsWindow(source.Handle)) Attach(taskbar);
            var dpiValue = GetDpiForWindow(taskbar) / 96d;
            var dpi = new DpiScale(dpiValue > 0 ? dpiValue : 1, dpiValue > 0 ? dpiValue : 1);
            var taskbarBounds = new Rect(bar.Left, bar.Top, Math.Max(0, bar.Right - bar.Left), Math.Max(0, bar.Bottom - bar.Top));
            var vertical = taskbarBounds.Height > taskbarBounds.Width;
            var rectangle = ComputePlacement(taskbarBounds, taskbarBounds, vertical ? 0u : 3u, settings.TaskbarLeftOffset, dpi);
            if (rectangle.IsEmpty) { ShowWindow(source!.Handle, 0); return; }
            if (rectangle != lastPlacement || !IsVisible)
            {
                SetLayout(vertical);
                surface.Width = rectangle.Width / dpi.DpiScaleX; surface.Height = rectangle.Height / dpi.DpiScaleY;
                if (!SetWindowPos(source!.Handle, IntPtr.Zero, (int)Math.Round(rectangle.X), (int)Math.Round(rectangle.Y),
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

    private void SetLayout(bool vertical)
    {
        name.Visibility = progress.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        content.ColumnDefinitions[0].Width = vertical ? new GridLength(1, GridUnitType.Star) : new GridLength(28);
        content.ColumnDefinitions[1].Width = vertical ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        surface.Padding = vertical ? new Thickness(2) : new Thickness(8, 3, 8, 3);
        icon.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        battery.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        battery.FontSize = vertical ? 10 : 13;
        Grid.SetColumn(name, 1); Grid.SetColumn(progress, 1); Grid.SetRow(progress, 1);
        Grid.SetColumn(battery, vertical ? 0 : 2); Grid.SetRow(battery, vertical ? 1 : 0);
        Grid.SetRowSpan(icon, vertical ? 1 : 2); Grid.SetRowSpan(battery, vertical ? 1 : 2);
    }

    internal static Rect ComputePlacement(Rect taskbar, Rect screen, uint edge, double offset, DpiScale dpi)
    {
        var visible = Rect.Intersect(taskbar, screen);
        if (visible.IsEmpty) return Rect.Empty;
        if (edge is 1 or 3)
        {
            if (visible.Height < 20 * dpi.DpiScaleY) return Rect.Empty;
            var width = Math.Min(192 * dpi.DpiScaleX, visible.Width);
            return new Rect(Math.Clamp(visible.Left + offset * dpi.DpiScaleX, visible.Left, visible.Right - width), visible.Top, width, visible.Height);
        }
        if (visible.Width < 20 * dpi.DpiScaleX) return Rect.Empty;
        var height = Math.Min(52 * dpi.DpiScaleY, visible.Height);
        return new Rect(visible.Left, Math.Clamp(visible.Top + offset * dpi.DpiScaleY, visible.Top, visible.Bottom - height), visible.Width, height);
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x001A or 0x031A) ApplyTaskbarTheme();
        if (message is 0x007E or 0x02E0 or 0x001A or 0x031A) { lastPlacement = Rect.Empty; surface.Dispatcher.BeginInvoke(new Action(UpdatePlacement)); }
        return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
