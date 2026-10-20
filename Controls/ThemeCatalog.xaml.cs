using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerHub.Plugins;
using PowerHub.Services;
using UserControl = System.Windows.Controls.UserControl;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PowerHub.Controls;

public partial class ThemeCatalog : UserControl
{
    private AppController? controller;
    private IReadOnlyList<PluginThemeEntry> entries = [];
    private List<ThemeRow> rows = [];
    private string language = "";
    private bool refreshing;

    public ThemeCatalog() => InitializeComponent();
    public void Initialize(AppController appController) { controller = appController; Refresh(); }

    public void Refresh()
    {
        if (controller is null) return;
        var available = controller.Plugins.AvailableThemes;
        Visibility = available.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        refreshing = true;
        try
        {
            if (!available.SequenceEqual(entries) || language != L.Current.Language)
            {
                entries = available;
                language = L.Current.Language;
                rows = entries.Select(entry => new ThemeRow(entry)).ToList();
                var selectedGroup = GroupCombo.SelectedValue as string ?? "";
                GroupCombo.ItemsSource = new[] { new GroupChoice("", L.T("全部游戏")) }.Concat(entries.Select(entry => entry.Group).Where(group => group.Length > 0).Distinct().Select(group => new GroupChoice(group, L.T(group)))).ToArray();
                GroupCombo.SelectedValue = selectedGroup;
                if (GroupCombo.SelectedIndex < 0) GroupCombo.SelectedIndex = 0;
                Filter();
            }
            SelectCurrent();
        }
        finally { refreshing = false; }
    }

    private void Filter()
    {
        if (ThemeList is null || controller is null) return;
        var group = GroupCombo.SelectedValue as string ?? "";
        var query = SearchBox.Text.Trim();
        var visible = rows.Where(row => (group.Length == 0 || row.Entry.Group == group) &&
            (query.Length == 0 || $"{row.Entry.Name} {row.Entry.Group} {row.Entry.Theme.Description} {row.Entry.Theme.Id} {row.Detail}".Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        var previous = refreshing;
        refreshing = true;
        try
        {
            ThemeList.ItemsSource = visible;
            ThemeList.Height = Math.Min(326, visible.Length * 79);
            CountText.Text = L.F("{0} / {1} 套", visible.Length, rows.Count);
            EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectCurrent();
        }
        finally { refreshing = previous; }
    }

    private void SelectCurrent()
    {
        var current = rows.FirstOrDefault(row => row.Entry.Value == controller?.Settings.Theme);
        ThemeList.SelectedItem = current;
        CurrentText.Text = current is null ? L.T("启用插件只添加主题，当前主题保持你的选择。") : L.F("当前主题：{0} · {1}", current.Name, L.T(current.Entry.Group));
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e) { if (!refreshing) Filter(); }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (!refreshing) Filter(); }
    private void OnThemeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || controller is null || ThemeList.SelectedItem is not ThemeRow row || controller.Settings.Theme == row.Entry.Value) return;
        controller.Settings.Theme = row.Entry.Value;
        controller.ApplySettings();
        Refresh();
    }

    private sealed record GroupChoice(string Value, string Label);
    private sealed class ThemeRow(PluginThemeEntry entry)
    {
        public PluginThemeEntry Entry { get; } = entry;
        public string Name { get; } = L.T(entry.Name);
        public string Detail { get; } = string.Join(" · ", new[] { L.T(entry.Group), entry.Theme.Description }.Where(text => text.Length > 0));
        public string Mode { get; } = L.T(entry.Theme.IsDark ? "深色" : "浅色");
        public SolidColorBrush Accent { get; } = Brush(entry.Theme, "Accent");
        public SolidColorBrush Secondary { get; } = Brush(entry.Theme, "TextSecondary");
        public SolidColorBrush Surface { get; } = Brush(entry.Theme, "Surface");
        private static SolidColorBrush Brush(PluginTheme theme, string key)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(theme.Colors.GetValueOrDefault(key, "#888888")));
            brush.Freeze();
            return brush;
        }
    }
}
