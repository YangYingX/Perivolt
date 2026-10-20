using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PowerHub.Controls;
using PowerHub.Models;
using PowerHub.Native;
using PowerHub.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Panel = System.Windows.Controls.Panel;

namespace PowerHub;

public sealed class TaskbarBatteryWindow : Window
{
    private readonly AppSettings settings;
    private readonly Border surface = new() { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(10, 4, 10, 4) };
    private readonly CardBatteryFill fill = new() { IsHitTestVisible = false };
    private readonly OutlinedText name = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 6, 0) };
    private readonly OutlinedText battery = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, MinWidth = 34 };
    private readonly System.Windows.Shapes.Path icon = new() { Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.6, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };

    public TaskbarBatteryWindow(AppSettings settings)
    {
        this.settings = settings;
        Width = 248; Height = 32; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(icon); content.Children.Add(name); Grid.SetColumn(battery, 1); content.Children.Add(battery);
        surface.Child = content;
        var root = new Grid(); root.Children.Add(surface); root.Children.Add(fill);
        Panel.SetZIndex(surface, 1); surface.Background = Brushes.Transparent;
        Content = root;
        SizeChanged += (_, _) => root.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 9, 9);
        SourceInitialized += (_, _) =>
        {
            OverlayNative.SetMousePassthrough(this, true);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnMessage);
        };
        Loaded += (_, _) => UpdatePlacement();
    }

    public void UpdateDevices(IReadOnlyList<DeviceStatus> devices)
    {
        var online = devices.Where(device => device.ConnectionKnown && device.IsConnected && device.HasBattery);
        var selected = settings.TaskbarDeviceId is { } id ? online.FirstOrDefault(device => device.Id == id)
            : online.OrderByDescending(device => device.IsBatteryLow == true).ThenBy(device => device.BatteryPercent ?? 101).FirstOrDefault();
        if (!settings.TaskbarEnabled || selected is null) { Hide(); return; }
        var appearance = new DeviceCard(selected, settings);
        Title = L.AppName;
        name.Text = appearance.Name; name.Foreground = appearance.NameBrush;
        battery.Text = selected.BatteryText; battery.Foreground = TryFindResource("TextPrimary") as Brush ?? Brushes.Black;
        battery.Visibility = appearance.BatteryPercentageVisibility;
        icon.Data = appearance.IconGeometry; icon.Stroke = appearance.NameBrush;
        name.IsOutlined = battery.IsOutlined = appearance.IsFillStyle;
        fill.Percent = selected.BatteryPercent; fill.Fill = appearance.BatteryBrush;
        fill.ColorOpacity = appearance.ColorOpacity; fill.FillOpacity = appearance.FillOpacity;
        fill.AnimationsEnabled = appearance.AnimationsEnabled;
        fill.VerticalAlignment = appearance.IsFillStyle ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        fill.Height = appearance.IsFillStyle ? double.NaN : 3;
        ((Grid)Content).Background = TryFindResource("Surface") as Brush;
        surface.BorderBrush = TryFindResource("Stroke") as Brush;
        if (!IsVisible) Show();
        UpdatePlacement();
    }

    public void UpdatePlacement()
    {
        if (!IsVisible) return;
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        if (SHAppBarMessage(5, ref data) == UIntPtr.Zero) return; // ABM_GETTASKBARPOS: read only, never reserve or modify the taskbar.
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bounds = screen.Bounds;
        var rectangle = ComputePlacement(new Rect(data.Rect.Left, data.Rect.Top, data.Rect.Right - data.Rect.Left, data.Rect.Bottom - data.Rect.Top),
            new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height), data.Edge, settings.TaskbarLeftOffset, dpi);
        OverlayNative.MovePixels(this, (int)rectangle.X, (int)rectangle.Y, (int)rectangle.Width, (int)rectangle.Height);
    }

    internal static Rect ComputePlacement(Rect taskbar, Rect screen, uint edge, double offset, DpiScale dpi)
    {
        var width = Math.Min(248 * dpi.DpiScaleX, screen.Width);
        var height = 32 * dpi.DpiScaleY;
        if (edge is 1 or 3)
        {
            height = Math.Min(height, taskbar.Height);
            return new Rect(Math.Clamp(taskbar.Left + offset * dpi.DpiScaleX, screen.Left, Math.Max(screen.Left, screen.Right - width)),
                Math.Clamp(taskbar.Top + (taskbar.Height - height) / 2, screen.Top, screen.Bottom - height), width, height);
        }
        return new Rect(Math.Clamp(edge == 0 ? taskbar.Right + 4 * dpi.DpiScaleX : taskbar.Left - width - 4 * dpi.DpiScaleX,
            screen.Left, Math.Max(screen.Left, screen.Right - width)), Math.Max(screen.Top, screen.Bottom - height - 8 * dpi.DpiScaleY), width, height);
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x007E or 0x02E0 or 0x001A) Dispatcher.BeginInvoke(new Action(UpdatePlacement));
        return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct AppBarData { public uint Size; public IntPtr Handle; public uint Callback; public uint Edge; public NativeRect Rect; public IntPtr Param; }
    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
}
