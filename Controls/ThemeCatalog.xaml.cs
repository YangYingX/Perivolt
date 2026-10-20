using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerHub.Plugins;
using PowerHub.Services;
using PowerHub.Models;
using System.Windows.Threading;
using System.ComponentModel;
using UserControl = System.Windows.Controls.UserControl;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PowerHub.Controls;

public partial class ThemeCatalog : UserControl
{
    private AppController? controller;
    private IReadOnlyList<PluginThemeEntry> entries = [];
    private List<ThemeRow> rows = [];
    private string language = "";
    private string? artworkDirectory;
    private bool refreshing;
    private readonly DispatcherTimer artworkTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public ThemeCatalog()
    {
        InitializeComponent();
        artworkTimer.Tick += (_, _) => { artworkTimer.Stop(); controller?.ApplySettings(); };
        Unloaded += (_, _) => { if (artworkTimer.IsEnabled) { artworkTimer.Stop(); controller?.ApplySettings(); } };
    }
    public void Initialize(AppController appController)
    {
        controller = appController;
        controller.Backdrop.Changed += RefreshArtwork;
        Refresh();
    }

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
                rows = entries.Select(entry => new ThemeRow(entry) { ArtworkPath = ThemeBackdrop.ResolvePath(controller.Settings, entry.Theme, entry.Value) }).ToList();
                artworkDirectory = controller.Settings.ThemeArtworkDirectory;
                var selectedGroup = GroupCombo.SelectedValue as string ?? "";
                GroupCombo.ItemsSource = new[] { new GroupChoice("", L.T("全部游戏")) }.Concat(entries.Select(entry => entry.Group).Where(group => group.Length > 0).Distinct().Select(group => new GroupChoice(group, L.T(group)))).ToArray();
                GroupCombo.SelectedValue = selectedGroup;
                if (GroupCombo.SelectedIndex < 0) GroupCombo.SelectedIndex = 0;
                Filter();
            }
            SelectCurrent();
            RefreshArtwork();
        }
        finally { refreshing = false; }
    }

    private void RefreshArtwork()
    {
        if (controller is null) return;
        var current = entries.FirstOrDefault(entry => entry.Value == controller.Settings.Theme);
        if (artworkDirectory != controller.Settings.ThemeArtworkDirectory)
        {
            artworkDirectory = controller.Settings.ThemeArtworkDirectory;
            foreach (var row in rows) row.ArtworkPath = ThemeBackdrop.ResolvePath(controller.Settings, row.Entry.Theme, row.Entry.Value);
        }
        if (current is not null)
        {
            var row = rows.First(item => item.Entry.Value == current.Value);
            row.ArtworkPath = ThemeBackdrop.ResolvePath(controller.Settings, current.Theme, current.Value);
        }
        ArtworkPanel.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        if (current is null) return;
        var previous = refreshing; refreshing = true;
        try
        {
            var option = controller.Settings.ThemeArtwork.GetValueOrDefault(current.Value) ?? new();
            ArtworkEnabledBox.IsChecked = option.Enabled;
            ArtworkOpacitySlider.Value = option.Opacity * 100;
            ArtworkOpacityText.Text = $"{Math.Round(option.Opacity * 100)}%";
            ArtworkPositionCombo.ItemsSource = new[] { new GroupChoice("Left", L.T("左侧")), new GroupChoice("Center", L.T("居中")), new GroupChoice("Right", L.T("右侧")) };
            ArtworkPositionCombo.SelectedValue = option.Position;
            ArtworkStatusText.Text = L.T(string.IsNullOrEmpty(controller.Backdrop.Status) ? "当前主题未找到立绘。" : controller.Backdrop.Status);
        }
        finally { refreshing = previous; }
    }

    private ThemeArtworkPreference? ArtworkOption()
    {
        if (controller is null || !entries.Any(entry => entry.Value == controller.Settings.Theme)) return null;
        if (!controller.Settings.ThemeArtwork.TryGetValue(controller.Settings.Theme, out var option))
            controller.Settings.ThemeArtwork[controller.Settings.Theme] = option = new();
        return option;
    }
    private void OnArtworkDirectory(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.T("选择立绘文件夹"), Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        controller.Settings.ThemeArtworkDirectory = dialog.FolderName;
        controller.Backdrop.Reload(); controller.ApplySettings(); RefreshArtwork();
    }
    private void OnArtworkFile(object sender, RoutedEventArgs e)
    {
        if (controller is null || ArtworkOption() is not { } option) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PNG / JPEG|*.png;*.jpg;*.jpeg", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        option.Path = dialog.FileName; controller.Backdrop.Reload(); controller.ApplySettings(); RefreshArtwork();
    }
    private void OnArtworkReset(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        controller.Settings.ThemeArtwork.Remove(controller.Settings.Theme);
        controller.Backdrop.Reload(); controller.ApplySettings(); RefreshArtwork();
    }
    private void OnArtworkChanged(object sender, RoutedEventArgs e)
    {
        if (refreshing || ArtworkOption() is not { } option) return;
        option.Enabled = ArtworkEnabledBox.IsChecked == true; controller?.ApplySettings(); RefreshArtwork();
    }
    private void OnArtworkOpacity(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (refreshing || ArtworkOption() is not { } option) return;
        option.Opacity = ArtworkOpacitySlider.Value / 100;
        ArtworkOpacityText.Text = $"{Math.Round(ArtworkOpacitySlider.Value)}%";
        if (controller is not null) controller.Backdrop.Apply(controller.Settings, controller.Plugins.ActiveTheme);
        artworkTimer.Stop(); artworkTimer.Start();
    }
    private void OnArtworkPosition(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || ArtworkOption() is not { } option || ArtworkPositionCombo.SelectedValue is not string position) return;
        option.Position = position; controller?.ApplySettings();
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
    private sealed class ThemeRow(PluginThemeEntry entry) : INotifyPropertyChanged
    {
        private string? artworkPath;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string? ArtworkPath
        {
            get => artworkPath;
            set { if (artworkPath == value) return; artworkPath = value; PropertyChanged?.Invoke(this, new(nameof(ArtworkPath))); }
        }
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
