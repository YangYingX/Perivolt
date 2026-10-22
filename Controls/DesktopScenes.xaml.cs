using System.Windows;
using System.Windows.Controls;
using PowerHub.Models;
using PowerHub.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerHub.Controls;

public partial class DesktopScenes : UserControl
{
    private AppController? controller;
    private (string Id, string Name)[] choices = [];
    private bool refreshing;
    private string statusKey = "先调整主题与桌面卡片，再命名保存。";
    private object[] statusArguments = [];

    public DesktopScenes() => InitializeComponent();
    public void Initialize(AppController appController) { controller = appController; Refresh(); }

    public void Refresh()
    {
        if (controller is null) return;
        var scenes = controller.Settings.DesktopScenes;
        var next = scenes.Select(scene => (scene.Id, scene.Name)).ToArray();
        if (!next.SequenceEqual(choices) || SceneCombo.ItemsSource is not DesktopScene[] current || !current.SequenceEqual(scenes))
        {
            var selected = SceneCombo.SelectedValue as string;
            var previousName = (SceneCombo.SelectedItem as DesktopScene)?.Name;
            var keepSelectionName = NameBox.Text.Length == 0 || NameBox.Text == previousName;
            refreshing = true;
            SceneCombo.ItemsSource = scenes.ToArray();
            SceneCombo.SelectedValue = selected;
            if (SceneCombo.SelectedIndex < 0 && scenes.Count > 0) SceneCombo.SelectedIndex = 0;
            refreshing = false;
            choices = next;
            if (keepSelectionName) NameBox.Text = (SceneCombo.SelectedItem as DesktopScene)?.Name ?? "";
        }
        StatusText.Text = L.F(statusKey, statusArguments);
        SetButtons();
    }

    private void SetStatus(string key, params object[] arguments)
    {
        statusKey = key; statusArguments = arguments;
        StatusText.Text = L.F(key, arguments);
    }
    private void SetButtons() => ApplyButton.IsEnabled = DeleteButton.IsEnabled = UpdateButton.IsEnabled = SceneCombo.SelectedItem is DesktopScene;
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || NameBox is null) return;
        if (SceneCombo.SelectedItem is DesktopScene scene) NameBox.Text = scene.Name;
        SetStatus("先调整主题与桌面卡片，再命名保存。");
        SetButtons();
    }
    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (SceneCombo.SelectedItem is DesktopScene scene && controller?.ApplyDesktopScene(scene.Id) == true)
            SetStatus("已切换到场景：{0}", scene.Name);
    }
    private void OnSave(object sender, RoutedEventArgs e) => Save(null);
    private void OnUpdate(object sender, RoutedEventArgs e) { if (SceneCombo.SelectedItem is DesktopScene scene) Save(scene.Id); }
    private void Save(string? id)
    {
        if (controller is null) return;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { SetStatus("请输入场景名称。"); NameBox.Focus(); return; }
        if (id is null && controller.Settings.DesktopScenes.Count >= DesktopScene.MaximumCount)
        { SetStatus("最多保存 12 个场景，请更新或删除已有场景。"); return; }
        var saved = controller.SaveDesktopScene(NameBox.Text, id);
        if (saved is null) return;
        Refresh();
        SceneCombo.SelectedValue = saved;
        SetStatus("场景已保存，包含当前主题、布局和显示设备。");
    }
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SceneCombo.SelectedItem is not DesktopScene scene || controller is null) return;
        controller.DeleteDesktopScene(scene.Id);
        Refresh();
        NameBox.Text = (SceneCombo.SelectedItem as DesktopScene)?.Name ?? "";
        SetStatus("场景已删除，当前桌面保持不变。");
    }
}
