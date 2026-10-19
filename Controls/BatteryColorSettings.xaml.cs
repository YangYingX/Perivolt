using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PowerHub.Models;
using PowerHub.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brush = System.Windows.Media.Brush;

namespace PowerHub.Controls;

public partial class BatteryColorSettings : UserControl
{
    private AppController? _controller;
    private string? _deviceId;
    private BatteryColorConfiguration _draft = new();
    private List<NodeRow> _rows = [];
    private bool _updating;
    private bool _dirty;
    private bool _colorsDirty;
    private bool _opacityDirty;
    private double _opacityDraft = 1;
    private double _loadedOpacity = 1;
    private bool _committing;
    private string _loadedSignature = "";
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };
    public event EventHandler? PreviewChanged;
    public Brush PreviewBrush => LowPreview.Foreground;
    public double PreviewColorOpacity => _opacityDraft;

    public BatteryColorSettings()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) => { if (!IsMouseCaptureWithin) CommitPending(); };
        Unloaded += (_, _) => CommitPending();
    }

    public void Initialize(AppController controller) { _controller = controller; LoadDraft(); }
    public void SetDevice(string? id, bool force = false)
    {
        if (!force && id == _deviceId) return;
        CommitPending(); _deviceId = id; LoadDraft();
    }
    public void DiscardPending() { _dirty = _colorsDirty = _opacityDirty = false; _saveTimer.Stop(); }
    public void Refresh()
    {
        if (_controller is null || _updating || _committing) return;
        if (!_dirty && !IsKeyboardFocusWithin && (Signature(_controller.Settings.GetBatteryColors(_deviceId)) != _loadedSignature || _controller.Settings.GetBatteryColorOpacity(_deviceId) != _loadedOpacity)) LoadDraft();
        else UpdatePreview();
    }
    private static string Signature(BatteryColorConfiguration colors) => $"{colors.GradientByBatteryLevel}/{colors.GradientWithinShape}/" +
        string.Join(";", colors.Stops.Select(stop => $"{stop.Position:R}:{stop.Color}:{stop.InterpolateToNext}"));
    private void LoadDraft()
    {
        if (_controller is null) return;
        _updating = true;
        try
        {
            _saveTimer.Stop(); _dirty = _colorsDirty = _opacityDirty = false;
            _draft = _controller.Settings.GetBatteryColors(_deviceId).Clone();
            _loadedSignature = Signature(_draft);
            _opacityDraft = _loadedOpacity = _controller.Settings.GetBatteryColorOpacity(_deviceId);
            ColorOpacitySlider.Value = Math.Round(_opacityDraft * 100);
            ColorOpacityBox.Text = ((int)ColorOpacitySlider.Value).ToString();
            OpacityErrorText.Visibility = Visibility.Collapsed;
            LevelGradientBox.IsChecked = _draft.GradientByBatteryLevel;
            ShapeGradientBox.IsChecked = _draft.GradientWithinShape;
            BuildRows();
            ErrorText.Visibility = Visibility.Collapsed;
            UpdatePreview();
        }
        finally { _updating = false; }
    }
    private void BuildRows()
    {
        _rows = _draft.Stops.OrderBy(stop => stop.Position).Select(stop => new NodeRow(stop, OnDraftChanged)).ToList();
        NodeItems.ItemsSource = _rows;
        UpdateNodeLabels();
        AddNodeButton.IsEnabled = _rows.Count < 10;
        NodeCountText.Text = $"{_rows.Count} / 10 · 至少保留 2 个";
    }
    private void UpdateNodeLabels()
    {
        var ordered = _rows.OrderBy(row => row.Position).ToList();
        for (var index = 0; index < ordered.Count; index++)
            ordered[index].UpdateLabel(_rows.IndexOf(ordered[index]) + 1, index + 1 < ordered.Count ? ordered[index + 1].Position : null, _rows.Count > 2);
    }
    private void OnDraftChanged()
    {
        if (_updating) return;
        _dirty = _colorsDirty = true;
        if (_rows.All(row => row.IsPositionValid)) ErrorText.Visibility = Visibility.Collapsed;
        UpdateNodeLabels();
        UpdatePreview();
        _saveTimer.Stop(); _saveTimer.Start();
    }
    private void OnColorOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || ColorOpacityBox is null) return;
        _updating = true; ColorOpacityBox.Text = ((int)Math.Round(ColorOpacitySlider.Value)).ToString(); _updating = false;
        OnOpacityDraftChanged();
    }
    private void OnColorOpacityInput(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_updating || ColorOpacitySlider is null) return;
        _saveTimer.Stop(); _dirty = _opacityDirty = true;
        if (!int.TryParse(ColorOpacityBox.Text, out var value) || value is < 0 or > 100) { OpacityErrorText.Visibility = Visibility.Visible; return; }
        _updating = true; ColorOpacitySlider.Value = value; _updating = false;
        OnOpacityDraftChanged();
    }
    private void OnOpacityDraftChanged()
    {
        _opacityDraft = ColorOpacitySlider.Value / 100;
        _dirty = _opacityDirty = true;
        OpacityErrorText.Visibility = Visibility.Collapsed;
        UpdatePreview(); _saveTimer.Stop(); _saveTimer.Start();
    }
    private void UpdatePreview()
    {
        if (_controller is null) return;
        var settings = _controller.Settings;
        var style = settings.GetDisplayStyle(_deviceId);
        var device = _controller.Devices.FirstOrDefault(device => device.Id == _deviceId && device.ConnectionKnown && device.IsConnected);
        var percent = _deviceId is null ? 65 : device?.BatteryPercent;
        LowPreview.Percent = percent;
        LowPreview.Foreground = BatteryPalette.GetBrush(_draft, percent);
        LowPreview.DisplayStyle = style;
        LowPreview.FillOpacity = settings.GetFillOpacity(_deviceId, style);
        LowPreview.ColorOpacity = _opacityDraft;
        LowPreview.AnimationIntensity = settings.GetAnimationIntensity(_deviceId);
        LowPreview.AnimationsEnabled = settings.GetAnimationsEnabled(_deviceId);
        LowPreview.IsCharging = device?.IsCharging == true;
        LowPreviewNumber.Text = _deviceId is null ? "65%" : device?.BatteryText ?? "—";
        LowPreviewNumber.Visibility = percent is null || settings.GetShowBatteryPercentage(_deviceId) ? Visibility.Visible : Visibility.Collapsed;
        ColorPreviewName.Text = device is null ? "默认外观" : settings.GetDeviceName(device);
        ColorPreviewName.Foreground = device is null ? TryFindResource("TextPrimary") as Brush : new DeviceCard(device, settings).NameBrush;
        ColorPreviewCaption.Text = _deviceId is null ? "示例电量 65% · 预览当前配色" : "真实设备电量 · 配色随编辑即时预览";
        ColorBand.Background = BatteryPalette.GetScaleBrush(_draft);
        ModeHint.Text = (_draft.GradientByBatteryLevel, _draft.GradientWithinShape) switch
        {
            (true,true) => "平滑区间显示渐变；关闭区间过渡后，用该区间上界颜色纯色显示。",
            (true,false) => "随电量在节点间变色；关闭过渡后使用上界档位颜色。",
            (false,true) => "平滑区间铺开完整色带；关闭过渡的区间显示该档纯色。",
            _ => "按上界分档使用纯色：默认 ≤20% 低电色、21–60% 中电色、61–100% 高电色。"
        };
        ResetColorsButton.Content = _deviceId is null ? "重置配色" : "重置为默认配色";
        InheritanceHint.Text = _deviceId is null ? "正在编辑默认配色；节点位置独立于充电提醒阈值。" :
            _dirty || settings.Devices.TryGetValue(_deviceId, out var preference) && (preference.BatteryColors is not null || preference.BatteryColorOpacity is not null)
                ? "此设备已使用独立配色，修改不会影响其他设备。" : "正在跟随默认配色；编辑任意节点后会保存为此设备的独立配色。";
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    public void CommitPending()
    {
        _saveTimer.Stop();
        if (_controller is null || _updating || !_dirty) return;
        if (!int.TryParse(ColorOpacityBox.Text, out var opacity) || opacity is < 0 or > 100)
        { OpacityErrorText.Visibility = Visibility.Visible; return; }
        if (_rows.Any(row => !row.IsPositionValid))
        {
            ErrorText.Text = "节点位置须为 0–100 的数字，本次无效输入尚未保存。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        var saved = _draft.Clone(); saved.Normalize();
        var colorsChanged = _colorsDirty;
        var opacityChanged = _opacityDirty;
        _colorsDirty = _opacityDirty = false;
        _dirty = false; _committing = true;
        try
        {
            if (_deviceId is null)
            {
                if (colorsChanged) _controller.Settings.BatteryColors = saved;
                if (opacityChanged) _controller.Settings.BatteryColorOpacity = _opacityDraft;
            }
            else
            {
                var preference = _controller.GetPreference(_deviceId);
                if (colorsChanged) preference.BatteryColors = saved;
                if (opacityChanged) preference.BatteryColorOpacity = _opacityDraft;
            }
            _loadedSignature = Signature(saved);
            _loadedOpacity = _opacityDraft;
            _controller.ApplySettings();
            ErrorText.Visibility = Visibility.Collapsed;
        }
        finally { _committing = false; }
        var orderedRows = _rows.OrderBy(row => row.Position).ToList();
        if (!_rows.SequenceEqual(orderedRows))
        {
            _rows = orderedRows;
            NodeItems.ItemsSource = _rows;
            UpdateNodeLabels();
        }
        UpdatePreview();
    }
    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _draft.GradientByBatteryLevel = LevelGradientBox.IsChecked == true;
        _draft.GradientWithinShape = ShapeGradientBox.IsChecked == true;
        OnDraftChanged(); CommitPending();
    }
    private void OnPositionLostFocus(object sender, RoutedEventArgs e) => CommitPending();
    private void OnPositionKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitPending(); e.Handled = true; } }
    private void OnNodeCaptureLost(object sender, MouseEventArgs e) { if (!_updating && !IsMouseCaptureWithin) CommitPending(); }
    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NodeRow row }) return;
        if (ColorPickerDialog.Pick(Window.GetWindow(this), row.Color, "颜色节点") is not { } color) return;
        row.Color = color;
        CommitPending();
    }
    private void OnAddNode(object sender, RoutedEventArgs e)
    {
        if (_rows.Count >= 10) return;
        if (_rows.Any(row => !row.IsPositionValid)) { CommitPending(); return; }
        var positions = _draft.Stops.Select(stop => stop.Position).Append(0).Append(100).Distinct().Order().ToArray();
        var gaps = positions.Zip(positions.Skip(1)).OrderByDescending(pair => pair.Second - pair.First).ToArray();
        var position = gaps.Length == 0 ? 50 : (gaps[0].First + gaps[0].Second) / 2;
        var color = _draft.Stops.OrderBy(stop => stop.Position).LastOrDefault(stop => stop.Position <= position)?.Color ?? _draft.Stops[0].Color;
        _draft.Stops.Add(new BatteryColorStop { Position = position, Color = color });
        BuildRows(); OnDraftChanged(); CommitPending();
    }
    private void OnRemoveNode(object sender, RoutedEventArgs e)
    {
        if (_rows.Count <= 2 || sender is not Button { DataContext: NodeRow row }) return;
        if (_rows.Any(item => !item.IsPositionValid)) { CommitPending(); return; }
        _draft.Stops.Remove(row.Stop); BuildRows(); OnDraftChanged(); CommitPending();
    }
    private void OnResetColors(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        DiscardPending();
        if (_deviceId is null) { _controller.Settings.BatteryColors = new BatteryColorConfiguration(); _controller.Settings.BatteryColorOpacity = 1; }
        else { var preference = _controller.GetPreference(_deviceId); preference.BatteryColors = null; preference.BatteryColorOpacity = null; }
        _controller.ApplySettings(); LoadDraft();
    }

    private sealed class NodeRow(BatteryColorStop stop, Action changed) : INotifyPropertyChanged
    {
        public BatteryColorStop Stop { get; } = stop;
        private string positionText = stop.Position.ToString("0.##", CultureInfo.InvariantCulture);
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        public double Position
        {
            get => Stop.Position;
            set
            {
                value = Math.Clamp(value, 0, 100);
                if (Stop.Position.Equals(value) && IsPositionValid) return;
                Stop.Position = value; positionText = value.ToString("0.##", CultureInfo.InvariantCulture);
                Notify(nameof(Position)); Notify(nameof(PositionText)); changed();
            }
        }
        public string PositionText
        {
            get => positionText;
            set
            {
                if (positionText == value) return;
                positionText = value;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var position) && double.IsFinite(position) && position is >= 0 and <= 100)
                { Stop.Position = position; Notify(nameof(Position)); }
                Notify(nameof(PositionText)); changed();
            }
        }
        public bool IsPositionValid => double.TryParse(positionText, NumberStyles.Float, CultureInfo.InvariantCulture, out var position) && double.IsFinite(position) && position is >= 0 and <= 100;
        public string Color { get => Stop.Color; set { if (Stop.Color == value) return; Stop.Color = value; Notify(nameof(Color)); Notify(nameof(ColorBrush)); changed(); } }
        public Brush ColorBrush { get { BatteryPalette.TryParseColor(Color, out var color); var brush = new SolidColorBrush(color); brush.Freeze(); return brush; } }
        public bool InterpolateToNext { get => Stop.InterpolateToNext; set { if (Stop.InterpolateToNext == value) return; Stop.InterpolateToNext = value; Notify(nameof(InterpolateToNext)); changed(); } }
        public string Label { get; private set; } = "";
        public string TransitionLabel { get; private set; } = "";
        public Visibility TransitionVisibility { get; private set; }
        public bool CanRemove { get; private set; }
        public void UpdateLabel(int number, double? next, bool canRemove)
        {
            Label = number.ToString(); TransitionLabel = next.HasValue ? $"{Position:0.##}–{next.Value:0.##}% 区间平滑过渡" : "";
            TransitionVisibility = next.HasValue ? Visibility.Visible : Visibility.Collapsed; CanRemove = canRemove;
            Notify(nameof(Label)); Notify(nameof(TransitionLabel)); Notify(nameof(TransitionVisibility)); Notify(nameof(CanRemove));
        }
    }
}
