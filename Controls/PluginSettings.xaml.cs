using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PowerHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PowerHub.Controls;

public partial class PluginSettings : UserControl
{
    private AppController? _controller;
    private bool _refreshing;

    public PluginSettings() => InitializeComponent();

    public void Initialize(AppController controller)
    {
        _controller = controller;
        Refresh();
    }

    public void Refresh()
    {
        if (_controller is null || IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox) return;
        _refreshing = true;
        try
        {
            var entries = _controller.Plugins.Entries;
            PluginItems.ItemsSource = entries.Select(entry => new PluginCard(entry.Id, L.T(entry.Name), L.T(entry.Description),
                entry.Kind, entry.Version, entry.IsEnabled, L.T(entry.Status),
                entry.Options.Select(option => new OptionEditor(entry.Id, option.Key, L.T(option.Label), option.Value)).ToList())).ToList();
            SummaryText.Text = L.F("{0} 个插件 · {1} 个已开启", entries.Count, entries.Count(entry => entry.IsEnabled));
            EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _refreshing = false; }
    }

    private bool Change(Action<PluginHost> change, string message)
    {
        if (_controller is null || _refreshing) return false;
        try
        {
            change(_controller.Plugins);
            _controller.ApplySettings();
            Refresh();
            StatusText.Text = message;
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Accent");
            return true;
        }
        catch (Exception error)
        {
            StatusText.Text = L.F("未能更新插件：{0}", error.Message);
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Danger");
            return false;
        }
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string id } box)
            Change(host => host.SetEnabled(id, box.IsChecked == true), L.T("插件状态已更新，会自动保存。"));
    }

    private void OnOptionLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) SaveOption(box);
    }

    private void OnOptionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        SaveOption(box);
        e.Handled = true;
    }

    private void SaveOption(TextBox box)
    {
        if (_controller is null || _refreshing || box.DataContext is not OptionEditor option || box.Text == option.Value) return;
        var value = box.Text;
        if (Change(host => host.SetOption(option.PluginId, option.Key, value), L.T("插件选项已保存。"))) option.Value = value;
    }

    private void OnReload(object sender, RoutedEventArgs e) =>
        Change(host => host.Reload(), L.T("插件目录已重新加载。"));

    private void OnOpenDirectory(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        try { Process.Start(new ProcessStartInfo(_controller.Plugins.DirectoryPath) { UseShellExecute = true }); }
        catch (Exception error)
        {
            StatusText.Text = L.F("未能打开插件目录：{0}", error.Message);
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Danger");
        }
    }

    private sealed class OptionEditor(string pluginId, string key, string label, string value)
    {
        public string PluginId { get; } = pluginId;
        public string Key { get; } = key;
        public string Label { get; } = label;
        public string Value { get; set; } = value;
    }

    private sealed record PluginCard(string Id, string Name, string Description, string Kind, string Version,
        bool IsEnabled, string Status, IReadOnlyList<OptionEditor> Options)
    {
        public string KindLabel => L.F("{0} · v{1}", (Kind switch { "theme" => L.T("外观插件"), "device" => L.T("设备适配"), _ => L.T("实用插件") }), Version);
        public string Icon => Kind switch { "theme" => "\uE790", "device" => "\uE772", _ => "\uE713" };
    }
}
