using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PowerHub.Models;
using PowerHub.Services;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Path = System.Windows.Shapes.Path;

namespace PowerHub;

public sealed class DeviceArrivalWindow : Window
{
    private readonly AppSettings settings;
    private readonly Action<string> openDevice;
    private readonly TextBlock heading = new() { FontSize = 10, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock name = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 4) };
    private readonly TextBlock detail = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock hint = new() { FontSize = 10, Margin = new Thickness(0, 7, 0, 0) };
    private readonly Path icon = new() { Width = 23, Height = 23, Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
    private readonly Button dismiss;
    private readonly Button openButton;
    private readonly Border surface;
    private readonly TranslateTransform translation = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DeviceStatus device;
    private double remaining = 5;
    private double previousTick;
    private bool dismissing;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public string DeviceId => device.Id;

    public DeviceArrivalWindow(AppSettings appSettings, DeviceStatus status, Action<string> onOpenDevice)
    {
        settings = appSettings;
        device = status;
        openDevice = onOpenDevice;
        Width = 370;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        UseLayoutRounding = true;
        SetResourceReference(ForegroundProperty, "TextPrimary");

        surface = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = new Thickness(18, 16, 12, 16), Margin = new Thickness(8), RenderTransform = translation };
        surface.SetResourceReference(Border.BackgroundProperty, "Surface");
        surface.SetResourceReference(Border.BorderBrushProperty, "Stroke");
        surface.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Opacity = .18, BlurRadius = 12, ShadowDepth = 2 };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        var iconBackground = new Border { Width = 34, Height = 38, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top, Child = icon };
        iconBackground.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        icon.SetResourceReference(Shape.StrokeProperty, "Accent");
        grid.Children.Add(iconBackground);
        var content = new StackPanel();
        content.Children.Add(heading); content.Children.Add(name); content.Children.Add(detail); content.Children.Add(hint);
        openButton = new Button { Content = content, Cursor = Cursors.Hand, Padding = new Thickness(0), Margin = new Thickness(0, 0, 5, 0), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        openButton.SetResourceReference(StyleProperty, "QuietButton");
        content.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = openButton });
        openButton.Click += (_, _) => openDevice(DeviceId);
        Grid.SetColumn(openButton, 1); grid.Children.Add(openButton);
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        dismiss = new Button { Content = "×", Width = 25, Height = 25, Padding = new Thickness(0), FontSize = 17, VerticalAlignment = VerticalAlignment.Top };
        dismiss.SetResourceReference(StyleProperty, "QuietButton");
        dismiss.Click += (_, _) => Dismiss();
        Grid.SetColumn(dismiss, 2); grid.Children.Add(dismiss);
        surface.Child = grid;
        Content = surface;
        SourceInitialized += (_, _) => PowerHub.Native.OverlayNative.SetMousePassthrough(this, false);
        Loaded += OnLoaded;
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { timer.Stop(); StopAnimations(); }
        };
        timer.Tick += OnTick;
        Closed += (_, _) =>
        {
            timer.Stop();
            StopAnimations();
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        };
        ApplySettings();
    }

    private bool AnimationsEnabled => settings.AnimationsEnabled && SystemParameters.ClientAreaAnimation;

    public void UpdateDevice(DeviceStatus status)
    {
        device = status;
        ApplySettings();
    }

    public void ApplySettings()
    {
        Title = L.T("设备已连接");
        heading.Text = L.T("设备已连接");
        name.Text = settings.GetDeviceName(device);
        name.ToolTip = name.Text;
        icon.Data = DeviceIcons.Get(device.Category);
        var battery = device.BatteryPercent is >= 0 and <= 100 ? $"{device.BatteryPercent}%"
            : !string.IsNullOrWhiteSpace(device.BatteryLevel) ? device.BatteryText : null;
        var now = DateTimeOffset.UtcNow;
        if (device.UpdatedAt is not { } updated || updated > now) battery = null;
        var cached = device.Source.Contains("缓存", StringComparison.OrdinalIgnoreCase) || device.Source.Contains("cache", StringComparison.OrdinalIgnoreCase)
            || device.UpdatedAt < now.AddMinutes(-5);
        var reported = device.Source.Contains("Windows", StringComparison.OrdinalIgnoreCase);
        var batteryText = battery is null ? L.T("电量未知") : L.F(cached ? "上次读数：{0}" : "电量：{0}", battery);
        if (reported && battery is not null) batteryText += " · " + L.T("系统报告");
        detail.Text = L.T(device.Connection) + " · " + batteryText;
        hint.Text = L.T("点击查看设备设置");
        System.Windows.Automation.AutomationProperties.SetName(openButton, name.Text + "，" + hint.Text);
        dismiss.ToolTip = L.T("关闭提示");
        System.Windows.Automation.AutomationProperties.SetName(dismiss, L.T("关闭提示"));
        if (!AnimationsEnabled && IsLoaded)
        {
            StopAnimations();
            if (dismissing) Close();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is not null)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var area = screen.WorkingArea;
            var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            PowerHub.Native.OverlayNative.MovePixels(this, Math.Max(area.Left, area.Right - width - 16), Math.Max(area.Top, area.Bottom - height - 16), width, height);
        }
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        if (AnimationsEnabled)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
            translation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
        if (!IsMouseOver) StartCountdown();
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) ApplySettings();
    }

    private void StartCountdown()
    {
        if (!IsVisible || dismissing) return;
        previousTick = Now;
        timer.Start();
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (timer.IsEnabled) remaining -= Now - previousTick;
        timer.Stop();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => StartCountdown();

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsVisible) { timer.Stop(); return; }
        var now = Now;
        remaining -= now - previousTick;
        previousTick = now;
        if (remaining <= 0) Dismiss();
    }

    private void Dismiss()
    {
        if (dismissing) return;
        dismissing = true;
        timer.Stop();
        if (!AnimationsEnabled) { Close(); return; }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void StopAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        translation.BeginAnimation(TranslateTransform.YProperty, null);
        Opacity = 1;
        translation.Y = 0;
    }
}
