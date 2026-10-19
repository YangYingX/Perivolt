using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PowerHub.Models;
using PowerHub.Services;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PowerHub.Controls;

public partial class BatteryColorSettings : UserControl
{
    private AppController? _controller;
    private bool _updating;

    public BatteryColorSettings() => InitializeComponent();

    public void Initialize(AppController controller)
    {
        _controller = controller;
        Refresh();
    }

    public void Refresh()
    {
        if (_controller is null || _updating) return;
        // Keep an unfinished edit intact when a background device scan updates the page.
        if (!IsKeyboardFocusWithin) RefreshFields();
        UpdatePreview();
    }

    private void RefreshFields()
    {
        if (_controller is null) return;
        _updating = true;
        var settings = _controller.Settings;
        LowThresholdBox.Text = settings.LowColorThreshold.ToString();
        MediumThresholdBox.Text = settings.MediumColorThreshold.ToString();
        LowStartBox.Text = settings.LowColorStart;
        LowEndBox.Text = settings.LowColorEnd;
        MediumStartBox.Text = settings.MediumColorStart;
        MediumEndBox.Text = settings.MediumColorEnd;
        HighStartBox.Text = settings.HighColorStart;
        HighEndBox.Text = settings.HighColorEnd;
        GradientBox.IsChecked = settings.BatteryGradientEnabled;
        _updating = false;
    }

    private void UpdatePreview()
    {
        if (_controller is null) return;
        var settings = _controller.Settings;
        BatteryPalette.Apply(settings);
        LowRangeText.Text = $"0–{settings.LowColorThreshold}%";
        MediumRangeText.Text = $"{settings.LowColorThreshold + 1}–{settings.MediumColorThreshold}%";
        HighRangeText.Text = settings.MediumColorThreshold == 100 ? "无剩余区间" : $"{settings.MediumColorThreshold + 1}–100%";
        SetSwatch(LowStartSwatch, settings.LowColorStart);
        SetSwatch(LowEndSwatch, settings.LowColorEnd);
        SetSwatch(MediumStartSwatch, settings.MediumColorStart);
        SetSwatch(MediumEndSwatch, settings.MediumColorEnd);
        SetSwatch(HighStartSwatch, settings.HighColorStart);
        SetSwatch(HighEndSwatch, settings.HighColorEnd);
        LowPreview.Foreground = BatteryPalette.GetBrush(20);
        MediumPreview.Foreground = BatteryPalette.GetBrush(50);
        HighPreview.Foreground = BatteryPalette.GetBrush(80);
    }

    private static void SetSwatch(Border swatch, string hex)
    {
        if (!BatteryPalette.TryParseColor(hex, out var color)) return;
        if (swatch.Background is SolidColorBrush current && current.Color == color) return;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        swatch.Background = brush;
    }

    private void OnInputLostFocus(object sender, RoutedEventArgs e) => CommitInputs();

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitInputs();
        e.Handled = true;
    }

    private void OnGradientClick(object sender, RoutedEventArgs e) => CommitInputs();

    private void CommitInputs()
    {
        if (_controller is null || _updating) return;
        if (!int.TryParse(LowThresholdBox.Text.Trim(), out var low) || !int.TryParse(MediumThresholdBox.Text.Trim(), out var medium) ||
            low is < 0 or > 100 || medium is < 0 or > 100 || low >= medium)
        {
            ShowError("颜色边界须为 0–100 的整数，且低电上限必须小于中电上限。");
            return;
        }
        var fields = new[] { LowStartBox, LowEndBox, MediumStartBox, MediumEndBox, HighStartBox, HighEndBox };
        var colors = fields.Select(field => AppSettings.NormalizeHexColor(field.Text)).ToArray();
        if (colors.Any(color => color is null))
        {
            ShowError("颜色须使用 #RRGGBB，例如 #8FE4C5；本次输入尚未保存。");
            return;
        }

        var settings = _controller.Settings;
        var gradient = GradientBox.IsChecked == true;
        var changed = settings.LowColorThreshold != low || settings.MediumColorThreshold != medium ||
            settings.LowColorStart != colors[0] || settings.LowColorEnd != colors[1] ||
            settings.MediumColorStart != colors[2] || settings.MediumColorEnd != colors[3] ||
            settings.HighColorStart != colors[4] || settings.HighColorEnd != colors[5] ||
            settings.BatteryGradientEnabled != gradient;
        settings.LowColorThreshold = low;
        settings.MediumColorThreshold = medium;
        settings.LowColorStart = colors[0]!;
        settings.LowColorEnd = colors[1]!;
        settings.MediumColorStart = colors[2]!;
        settings.MediumColorEnd = colors[3]!;
        settings.HighColorStart = colors[4]!;
        settings.HighColorEnd = colors[5]!;
        settings.BatteryGradientEnabled = gradient;
        ErrorText.Visibility = Visibility.Collapsed;
        if (changed) _controller.ApplySettings();
        RefreshFields();
        UpdatePreview();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (_controller is null || sender is not Button { Tag: string key }) return;
        var field = key switch
        {
            "LowColorStart" => LowStartBox, "LowColorEnd" => LowEndBox,
            "MediumColorStart" => MediumStartBox, "MediumColorEnd" => MediumEndBox,
            "HighColorStart" => HighStartBox, "HighColorEnd" => HighEndBox, _ => null
        };
        if (field is null) return;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (BatteryPalette.TryParseColor(field.Text.Trim(), out var current))
            dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        field.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        CommitInputs();
    }
}
