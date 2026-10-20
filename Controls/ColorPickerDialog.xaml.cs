using PowerHub.Services;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PowerHub.Models;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PowerHub.Controls;

public partial class ColorPickerDialog : Window
{
    private bool updating;
    private bool ready;
    public string SelectedColor { get; private set; }

    public ColorPickerDialog(string? initialColor, string? title = null)
    {
        var normalized = AppSettings.NormalizeHexColor(initialColor);
        if (normalized is null && initialColor is { Length: 9 } && initialColor[0] == '#' &&
            uint.TryParse(initialColor.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            normalized = $"#{argb & 0xFFFFFF:X6}";
        SelectedColor = normalized ?? "#8FE4C5";
        InitializeComponent();
        Title = Heading.Text = title ?? L.T("选择颜色");
        var initial = Parse(SelectedColor);
        OriginalSwatch.Background = Brush(initial);
        OriginalSwatch.ToolTip = SelectedColor;
        ColorPlane.SelectionChanged += UpdateFromPlane;
        foreach (var hex in new[] { "#8FE4C5", "#78C9FF", "#A993E2", "#F19AAB", "#F2C27B", "#D95073", "#F0F4FA", "#18253B" })
        {
            var swatch = new Button
            {
                Background = Brush(Parse(hex)), Height = 30, Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(0), ToolTip = hex, Tag = hex
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, L.F("选择颜色 {0}", hex));
            swatch.Click += (_, _) => SetColor(Parse(hex));
            Swatches.Children.Add(swatch);
        }
        ready = true;
        SetColor(initial);
    }

    public static string? Pick(Window? owner, string? initialColor, string? title = null)
    {
        var dialog = new ColorPickerDialog(initialColor, title);
        if (owner is not null) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.SelectedColor : null;
    }

    private void SetColor(Color color, bool updateText = true)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0 ? HueSlider.Value : max == r ? 60 * ((g - b) / delta % 6)
            : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        updating = true;
        HueSlider.Value = hue;
        ColorPlane.SetSelection(hue, max == 0 ? 0 : delta / max, max);
        updating = false;
        UpdatePreview(color, updateText);
    }

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || updating) return;
        ColorPlane.SetSelection(e.NewValue, ColorPlane.Saturation, ColorPlane.Value);
        UpdateFromPlane();
    }

    private void UpdateFromPlane() => UpdatePreview(FromHsv(ColorPlane.Hue, ColorPlane.Saturation, ColorPlane.Value), true);

    private void UpdatePreview(Color color, bool updateText)
    {
        SelectedColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        NewSwatch.Background = Brush(color);
        HueSlider.Foreground = Brush(FromHsv(ColorPlane.Hue, 1, 1));
        RgbText.Text = $"RGB  {color.R} · {color.G} · {color.B}";
        if (updateText)
        {
            updating = true;
            HexInput.Text = SelectedColor;
            updating = false;
        }
        ConfirmButton.IsEnabled = true;
        InputError.Visibility = Visibility.Collapsed;
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || updating) return;
        if (AppSettings.NormalizeHexColor(HexInput.Text) is { } hex) SetColor(Parse(hex), false);
        else
        {
            ConfirmButton.IsEnabled = false;
            InputError.Visibility = Visibility.Visible;
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (ConfirmButton.IsEnabled) DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    internal static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static Color FromHsv(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var h = (hue % 360) / 60;
        var x = chroma * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h switch
        {
            < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        var m = value - chroma;
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

public sealed class ColorSelectionPlane : FrameworkElement
{
    private static readonly LinearGradientBrush WhiteOverlay = Gradient(Colors.White, Colors.Transparent, new Point(1, 0));
    private static readonly LinearGradientBrush BlackOverlay = Gradient(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 1));
    private static readonly Pen LightMarker = FrozenPen(Colors.White, 3);
    private static readonly Pen DarkMarker = FrozenPen(Color.FromArgb(150, 0, 0, 0), 1);
    private SolidColorBrush hueBrush = ColorPickerDialog.Brush(Colors.Red);
    public double Hue { get; private set; }
    public double Saturation { get; private set; }
    public double Value { get; private set; }
    public event Action? SelectionChanged;

    public void SetSelection(double hue, double saturation, double value)
    {
        if (Hue != hue) hueBrush = ColorPickerDialog.Brush(ColorPickerDialog.FromHsv(hue, 1, 1));
        Hue = hue;
        Saturation = Math.Clamp(saturation, 0, 1);
        Value = Math.Clamp(value, 0, 1);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var area = ColorArea;
        drawing.DrawRoundedRectangle(hueBrush, null, area, 10, 10);
        drawing.DrawRoundedRectangle(WhiteOverlay, null, area, 10, 10);
        drawing.DrawRoundedRectangle(BlackOverlay, null, area, 10, 10);
        if (IsKeyboardFocused && TryFindResource("Accent") is System.Windows.Media.Brush accent)
            drawing.DrawRoundedRectangle(null, new Pen(accent, 2), new Rect(2, 2, Math.Max(0, ActualWidth - 4), Math.Max(0, ActualHeight - 4)), 14, 14);
        var point = new Point(area.Left + Saturation * area.Width, area.Top + (1 - Value) * area.Height);
        drawing.DrawEllipse(null, DarkMarker, point, 7, 7);
        drawing.DrawEllipse(null, LightMarker, point, 5, 5);
    }

    private Rect ColorArea => new(7, 7, Math.Max(1, ActualWidth - 14), Math.Max(1, ActualHeight - 14));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        SelectAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) SelectAt(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) { SelectAt(e.GetPosition(this)); ReleaseMouseCapture(); }
    }

    private void SelectAt(Point point)
    {
        var area = ColorArea;
        SetSelection(Hue, (point.X - area.Left) / area.Width, 1 - (point.Y - area.Top) / area.Height);
        SelectionChanged?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
        var (saturation, value) = e.Key switch
        {
            Key.Left => (Saturation - step, Value), Key.Right => (Saturation + step, Value),
            Key.Up => (Saturation, Value + step), Key.Down => (Saturation, Value - step), _ => (Saturation, Value)
        };
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        SetSelection(Hue, saturation, value);
        SelectionChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    private static LinearGradientBrush Gradient(Color start, Color end, Point endPoint)
    {
        var brush = new LinearGradientBrush(start, end, new Point(0, 0), endPoint);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(ColorPickerDialog.Brush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
