using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PowerHub.Controls;
using PowerHub.Models;
using PowerHub.Native;
using PowerHub.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using TextBlock = System.Windows.Controls.TextBlock;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PowerHub;

public sealed class TaskbarBatteryWindow : Window
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

    public TaskbarBatteryWindow(AppSettings settings)
    {
        this.settings = settings;
        Width = 192; Height = 48; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        UseLayoutRounding = true; SnapsToDevicePixels = true; FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(icon); content.Children.Add(name); content.Children.Add(battery);
        progress.Children.Add(track); progress.Children.Add(fill); content.Children.Add(progress);
        surface.Child = content;
        Content = surface;
        SourceInitialized += (_, _) =>
        {
            OverlayNative.SetMousePassthrough(this, true);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnMessage);
        };
        Loaded += (_, _) => UpdatePlacement();
        positionTimer.Tick += (_, _) => UpdatePlacement();
        Closed += (_, _) => positionTimer.Stop();
    }

    public void UpdateDevices(IReadOnlyList<DeviceStatus> devices)
    {
        var online = devices.Where(device => device.ConnectionKnown && device.IsConnected && device.HasBattery);
        var selected = settings.TaskbarDeviceId is { } id ? online.FirstOrDefault(device => device.Id == id)
            : online.OrderByDescending(device => device.IsBatteryLow == true).ThenBy(device => device.BatteryPercent ?? 101).FirstOrDefault();
        if (!settings.TaskbarEnabled || selected is null) { Suspend(); return; }
        var appearance = new DeviceCard(selected, settings);
        Title = L.AppName;
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
        new WindowInteropHelper(this).EnsureHandle();
        positionTimer.Start();
        UpdatePlacement();
    }

    public void Suspend() { positionTimer.Stop(); Hide(); lastPlacement = Rect.Empty; }

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
            if (taskbar == IntPtr.Zero || !IsWindowVisible(taskbar) || !GetWindowRect(taskbar, out var bar)) { Hide(); return; }
            var screen = System.Windows.Forms.Screen.FromHandle(taskbar).Bounds;
            var dpiValue = GetDpiForWindow(taskbar) / 96d;
            var dpi = new DpiScale(dpiValue > 0 ? dpiValue : 1, dpiValue > 0 ? dpiValue : 1);
            var bounds = new Rect(screen.Left, screen.Top, screen.Width, screen.Height);
            var taskbarBounds = new Rect(bar.Left, bar.Top, Math.Max(0, bar.Right - bar.Left), Math.Max(0, bar.Bottom - bar.Top));
            var vertical = taskbarBounds.Height > taskbarBounds.Width;
            var rectangle = ComputePlacement(taskbarBounds, bounds, vertical ? 0u : 3u, settings.TaskbarLeftOffset, dpi);
            if (rectangle.IsEmpty) { Hide(); return; }
            if (rectangle != lastPlacement || !IsVisible)
            {
                SetLayout(vertical);
                Width = rectangle.Width / dpi.DpiScaleX; Height = rectangle.Height / dpi.DpiScaleY;
                OverlayNative.MovePixels(this, (int)Math.Round(rectangle.X), (int)Math.Round(rectangle.Y), (int)Math.Round(rectangle.Width), (int)Math.Round(rectangle.Height));
                if (!IsVisible) Show();
                lastPlacement = rectangle;
            }
        }
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
        if (message is 0x007E or 0x02E0 or 0x001A or 0x031A) { lastPlacement = Rect.Empty; Dispatcher.BeginInvoke(new Action(UpdatePlacement)); }
        return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}
