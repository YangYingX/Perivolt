using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using PowerHub.Mac.Controls;
using PowerHub.Models;
using PowerHub.Services;

namespace PowerHub.Mac;

public sealed class DesktopWidgetWindow : Window
{
    private readonly AppController controller;
    private readonly DispatcherTimer savePosition = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool closeForExit, refreshing, dragged, positionDirty;
    private bool positionApplied;

    public DesktopWidgetWindow(AppController controller)
    {
        this.controller = controller;
        FontFamily = MacThemePalette.InterfaceFont;
        Title = L.T("PowerHub 桌面卡片");
        Width = 330;
        MinWidth = 290;
        MaxHeight = 720;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        SizeToContent = SizeToContent.Height;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        ActualThemeVariantChanged += (_, _) => { if (!refreshing && controller.Settings.Theme == "System") Refresh(); };
        Opened += (_, _) => ApplyPosition();
        PositionChanged += (_, _) =>
        {
            if (refreshing || !dragged) return;
            positionDirty = true;
            savePosition.Stop();
            savePosition.Start();
        };
        PointerReleased += (_, _) => { SavePosition(); dragged = false; };
        savePosition.Tick += (_, _) => SavePosition();
        Closing += (_, e) =>
        {
            if (closeForExit || controller.IsExiting) return;
            e.Cancel = true;
            controller.Settings.WidgetEnabled = false;
            controller.ApplySettings();
        };
        Closed += (_, _) => savePosition.Stop();
        Refresh();
    }

    public void CloseForExit()
    {
        SavePosition();
        closeForExit = true;
        savePosition.Stop();
        Close();
    }

    public void Refresh()
    {
        if (closeForExit) return;
        refreshing = true;
        try
        {
            var settings = controller.Settings;
            var palette = MacThemePalette.Create(settings.Theme);
            RequestedThemeVariant = settings.Theme == "System" ? ThemeVariant.Default : palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
            Topmost = settings.WidgetTopmost;
            Title = L.T("PowerHub 桌面卡片");
            var body = new StackPanel { Spacing = settings.WidgetCompact ? 5 : 10 };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 4 };
            var dragArea = new TextBlock { Text = L.T("桌面电量"), Foreground = palette.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 8) };
            dragArea.PointerPressed += (_, e) =>
            {
                if (settings.WidgetLocked || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                dragged = true;
                BeginMoveDrag(e);
                e.Handled = true;
            };
            header.Children.Add(dragArea);
            var unlock = SmallButton(settings.WidgetLocked ? L.T("解锁位置") : L.T("锁定位置"), () =>
            {
                settings.WidgetLocked = !settings.WidgetLocked;
                controller.ApplySettings();
            }, palette);
            Grid.SetColumn(unlock, 1);
            header.Children.Add(unlock);
            var open = SmallButton("↗", controller.ShowMain, palette);
            ToolTip.SetTip(open, L.T("打开电量中心"));
            Grid.SetColumn(open, 2);
            header.Children.Add(open);
            var hide = SmallButton("×", () => { settings.WidgetEnabled = false; controller.ApplySettings(); }, palette);
            ToolTip.SetTip(hide, L.T("隐藏桌面卡片"));
            Grid.SetColumn(hide, 3);
            header.Children.Add(hide);
            body.Children.Add(header);
            var devices = controller.Devices.Where(device => device.ConnectionKnown && device.IsConnected && settings.IsPinned(device.Id))
                .OrderBy(device => settings.GetDeviceOrder(device.Id)).ThenBy(device => settings.GetDeviceName(device), StringComparer.CurrentCultureIgnoreCase).ToArray();
            foreach (var device in devices) body.Children.Add(DeviceRow(device, palette));
            if (devices.Length == 0) body.Children.Add(new TextBlock { Text = L.T("当前没有已连接的固定设备"), Foreground = palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 14) });
            var color = ((SolidColorBrush)palette.Surface).Color;
            var background = settings.WidgetTransparent ? new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B)) : palette.Surface;
            Content = new Border
            {
                CornerRadius = new CornerRadius(16), BorderBrush = palette.Stroke, BorderThickness = new Thickness(1),
                Padding = new Thickness(settings.WidgetCompact ? 10 : 14), Background = background,
                Child = new ScrollViewer { Content = body, MaxHeight = 670, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
            };
            ApplyPosition();
        }
        finally { refreshing = false; }
    }

    private Control DeviceRow(DeviceStatus device, MacThemePalette palette)
    {
        var compact = controller.Settings.WidgetCompact;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(compact ? "74,*" : "92,*"), ColumnSpacing = 8 };
        row.Children.Add(new BatteryVisual(controller.Settings, device.Id, device) { Width = compact ? 72 : 88, Height = compact ? 58 : 76 });
        var labels = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = controller.Settings.GetDeviceName(device), Foreground = palette.Text, FontWeight = FontWeight.SemiBold,
            FontSize = compact ? 12 : 13, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!compact)
        {
            labels.Children.Add(new TextBlock { Text = device.ChargingText, Foreground = palette.Muted, FontSize = 11 });
            labels.Children.Add(new TextBlock { Text = L.T(device.Connection), Foreground = palette.Muted, FontSize = 10 });
        }
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        return new Border { Background = palette.SurfaceAlt, CornerRadius = new CornerRadius(12), Padding = new Thickness(8, compact ? 3 : 6), Child = row };
    }

    private static Button SmallButton(string text, Action action, MacThemePalette palette)
    {
        var button = new Button { Content = text, FontSize = 10, Padding = new Thickness(7, 5), Foreground = palette.Muted, Background = Brushes.Transparent };
        button.Click += (_, _) => action();
        return button;
    }

    private void ApplyPosition()
    {
        if (!IsVisible) return;
        var settings = controller.Settings;
        if (settings.WidgetLeft is double left && settings.WidgetTop is double top)
        {
            if (!positionApplied) Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
            positionApplied = true;
            EnsureVisible();
            return;
        }
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        Position = new PixelPoint(Math.Max(area.X, area.Right - (int)Math.Ceiling(Width * screen.Scaling) - 24), area.Y + 24);
        positionApplied = true;
    }

    private void EnsureVisible()
    {
        if (Screens.All.Any(screen => new PixelRect(Position.X, Position.Y, 100, 40).Intersects(screen.WorkingArea))) return;
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is not null) Position = new PixelPoint(screen.WorkingArea.X + 24, screen.WorkingArea.Y + 24);
    }

    private void SavePosition()
    {
        savePosition.Stop();
        if (!positionDirty || refreshing || closeForExit) return;
        positionDirty = false;
        controller.Settings.WidgetLeft = Position.X;
        controller.Settings.WidgetTop = Position.Y;
        controller.ApplySettings();
    }
}
