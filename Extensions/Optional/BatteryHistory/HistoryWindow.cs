using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Perivolt.Extensions.BatteryHistory;

internal sealed class HistoryWindow : Window
{
    private sealed record DeviceChoice(string IdHash, string Name);
    private readonly BatteryHistoryPlugin _plugin;
    private readonly ComboBox _devices = new() { MinWidth = 180, DisplayMemberPath = nameof(DeviceChoice.Name), Margin = new(0, 0, 12, 0) };
    private readonly ComboBox _range = new() { ItemsSource = new[] { "最近 24 小时", "最近 7 天" }, SelectedIndex = 0, Width = 124 };
    private readonly HistoryChart _chart = new();
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 48, Margin = new(0, 8, 0, 0) };
    private string? _selected;
    private bool _refreshing;

    internal HistoryWindow(BatteryHistoryPlugin plugin)
    {
        _plugin = plugin;
        Title = "电量历史 · 电栖";
        Width = 760; Height = 480; MinWidth = 620; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new("Segoe UI, Microsoft YaHei UI");
        SetResourceReference(BackgroundProperty, "PageBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        var layout = new Grid { Margin = new(20) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            layout.RowDefinitions.Add(new() { Height = height });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "电量历史", FontSize = 22, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock { Text = "百分比采样趋势 · 本地时间 · 离线及长间隔不连线", FontSize = 12, Margin = new(0, 5, 0, 0) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        heading.Children.Add(subtitle);
        layout.Children.Add(heading);

        var toolbar = new Grid { Margin = new(0, 14, 0, 12) };
        toolbar.ColumnDefinitions.Add(new());
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.Children.Add(_devices);
        Grid.SetColumn(_range, 1); toolbar.Children.Add(_range);
        var clear = new Button { Content = "清空本地历史", Margin = new(12, 0, 0, 0), Padding = new(12, 8, 12, 8) };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "清空全部设备已保存的本地电量历史？此操作无法撤销。", "清空电量历史",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) _plugin.ClearHistory();
        };
        Grid.SetColumn(clear, 2); toolbar.Children.Add(clear);
        Grid.SetRow(toolbar, 1); layout.Children.Add(toolbar);

        var chartFrame = new Border { CornerRadius = new(12), BorderThickness = new(1), Padding = new(12), Child = _chart, MinHeight = 120 };
        chartFrame.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        chartFrame.SetResourceReference(Border.BorderBrushProperty, "Stroke");
        Grid.SetRow(chartFrame, 2); layout.Children.Add(chartFrame);
        Grid.SetRow(_detail, 3); layout.Children.Add(_detail);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetRow(_status, 4); layout.Children.Add(_status);
        Content = layout;
        _devices.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _selected = (_devices.SelectedItem as DeviceChoice)?.IdHash;
            Refresh();
        };
        _range.SelectionChanged += (_, _) => { if (!_refreshing) Refresh(); };
        Refresh();
    }

    internal void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var devices = _plugin.Devices;
            var selected = devices.FirstOrDefault(d => d.IdHash == _selected) ?? devices.FirstOrDefault();
            _selected = selected?.IdHash;
            var choices = devices.Select(d => new DeviceChoice(d.IdHash, d.Name)).ToArray();
            _devices.ItemsSource = choices;
            _devices.SelectedItem = choices.FirstOrDefault(c => c.IdHash == _selected);
            _devices.IsEnabled = choices.Length > 0;
            var end = DateTimeOffset.UtcNow;
            var start = end - (_range.SelectedIndex == 1 ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24));
            var points = selected?.Samples.Where(s => s.At >= start && s.At <= end).ToArray() ?? [];
            _chart.SetData(points, start, end, selected is null ? "启用后才开始记录。\n等待新的有效电量百分比。" : "此时间范围内暂无样本。\n可切换 7 天查看。");
            _detail.Text = points.Length == 0 ? "仅显示实际采样点，不估算功率、能耗或剩余使用时长。" :
                $"{points.Length} 条样本 · 最后样本 {points[^1].At.ToLocalTime():MM-dd HH:mm:ss} · {points[^1].Percent}%" +
                (points.Any(p => p.SystemReported) ? "\n含 Windows 系统报告，可能受驱动缓存影响，不代表设备实时上报。" : "");
            _status.Text = _plugin.Status;
            _status.ToolTip = _plugin.Status;
        }
        catch (Exception exception) { _status.Text = "无法显示趋势：" + exception.Message; }
        finally { _refreshing = false; }
    }
}
