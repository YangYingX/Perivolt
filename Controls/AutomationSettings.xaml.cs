using System.Windows;
using System.Windows.Controls;
using PowerHub.Models;
using PowerHub.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerHub.Controls;

public partial class AutomationSettings : UserControl
{
    private AppController? controller;
    private bool refreshing;
    private string language = "";
    private string status = "";
    private RuleRow[] ruleRows = [];
    private AutomationLogEntry[] history = [];

    public AutomationSettings() => InitializeComponent();
    public void Initialize(AppController appController) { controller = appController; Refresh(); }

    public void Refresh()
    {
        if (controller is null || refreshing) return;
        refreshing = true;
        try
        {
            var translated = language != L.Current.Language;
            if (translated)
            {
                language = L.Current.Language;
                TemplateCombo.ItemsSource = new[] { new Choice("", L.T("从模板创建")), new Choice("headset", L.T("耳机联动")), new Choice("game", L.T("游戏模式")), new Choice("night", L.T("夜间模式")) };
                TemplateCombo.SelectedIndex = 0;
                status = "";
            }
            EnabledBox.IsChecked = controller.Settings.AutomationEnabled;
            StateText.Text = L.T(!controller.Settings.AutomationEnabled ? "自动化已暂停" : controller.Automation.IsExecuting ? "正在执行联动" : "自动化正在等待触发");
            var rules = controller.Settings.AutomationRules;
            var rows = rules.Select(rule => new RuleRow(rule.Id, rule.Name, rule.Enabled,
                AutomationLabels.TriggerSummary(controller, rule),
                L.F("{0} 个附加条件 · {1} 个顺序动作 · 冷却 {2} 秒", rule.Conditions.Count, rule.Actions.Count, rule.CooldownSeconds) +
                    (rule.RestoreOnEnd ? " · " + L.T("结束后自动恢复") : ""),
                controller.Settings.AutomationEnabled && rule.Enabled && !controller.Automation.IsExecuting,
                controller.Automation.HasRestorePoint(rule.Id) && !controller.Automation.IsExecuting)).ToArray();
            if (!rows.SequenceEqual(ruleRows)) { ruleRows = rows; RuleItems.ItemsSource = rows; }
            EmptyState.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RestoreAllButton.IsEnabled = !controller.Automation.IsExecuting && controller.Automation.RestorePointCount > 0;
            AudioStatusText.Text = controller.Automation.AudioError ?? L.T("音频输出可在编辑规则时选择；需要时点击刷新。");
            ProcessStatusText.Text = controller.Automation.ProcessError ?? "";
            StatusText.Text = status;
            var entries = controller.Automation.History.OrderByDescending(entry => entry.At).Take(200).ToArray();
            if (translated || !entries.SequenceEqual(history))
            {
                history = entries;
                HistoryItems.ItemsSource = entries.Select(entry => new HistoryRow(
                    $"{entry.At.LocalDateTime:MM-dd HH:mm:ss} · {entry.RuleName} · {L.T(entry.Status)}",
                    entry.Reason + (entry.Steps.Count == 0 ? "" : "\n" + string.Join("\n", entry.Steps)))).ToArray();
            }
            HistoryEmptyText.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryErrorText.Text = controller.Automation.HistoryError ?? "";
        }
        finally { refreshing = false; }
    }

    private void OnEnabled(object sender, RoutedEventArgs e)
    {
        if (controller is null || refreshing) return;
        controller.Settings.AutomationEnabled = EnabledBox.IsChecked == true;
        controller.ApplySettings();
    }
    private void OnRuleEnabled(object sender, RoutedEventArgs e)
    {
        if (controller is not null && sender is CheckBox { Tag: string id } box) controller.SetAutomationRuleEnabled(id, box.IsChecked == true);
    }
    private AutomationRule? Selected(object sender) => sender is Button { Tag: string id } ? controller?.Settings.AutomationRules.FirstOrDefault(rule => rule.Id == id) : null;
    private void OnNew(object sender, RoutedEventArgs e) => Edit(new AutomationRule { Name = L.T("新规则"), Actions = [new() { Kind = AutomationActionKind.ApplyScene }] });
    private void OnEdit(object sender, RoutedEventArgs e) { if (Selected(sender) is { } rule) Edit(rule.Clone()); }
    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (Selected(sender) is not { } rule) return;
        var copy = rule.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.Name = L.F("{0} 副本", rule.Name); copy.Enabled = false; Edit(copy);
    }
    private void Edit(AutomationRule draft)
    {
        if (controller is null) return;
        if (!controller.Settings.AutomationRules.Any(rule => rule.Id == draft.Id) && controller.Settings.AutomationRules.Count >= AutomationRule.MaximumCount)
        { SetStatus(L.T("最多保存 32 条规则，请先删除不再使用的规则。")); return; }
        controller.Automation.RefreshAudio();
        var editor = new AutomationRuleEditorWindow(controller, draft) { Owner = Window.GetWindow(this) };
        editor.ShowDialog();
        Refresh();
    }
    private void OnTemplate(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || controller is null || TemplateCombo.SelectedValue is not string { Length: > 0 } template) return;
        refreshing = true;
        try { TemplateCombo.SelectedIndex = 0; }
        finally { refreshing = false; }
        controller.Automation.RefreshAudio();
        var scene = controller.Settings.DesktopScenes.FirstOrDefault()?.Id ?? "";
        var rule = new AutomationRule { Enabled = false };
        switch (template)
        {
            case "headset":
                rule.Name = L.T("耳机联动"); rule.Trigger = AutomationTrigger.DeviceConnected; rule.RestoreOnEnd = true;
                rule.DeviceId = controller.Devices.FirstOrDefault(device => device.Category is "耳机" or "音频设备")?.Id ?? "";
                rule.Actions = [new() { Kind = AutomationActionKind.SetAudioOutput, TargetId = controller.Automation.Outputs.FirstOrDefault()?.Id ?? "" }, new() { Kind = AutomationActionKind.ApplyScene, TargetId = scene }];
                break;
            case "game":
                rule.Name = L.T("游戏模式"); rule.Trigger = AutomationTrigger.ProcessStarted; rule.RestoreOnEnd = true;
                rule.Actions = [new() { Kind = AutomationActionKind.ApplyScene, TargetId = scene }, new() { Kind = AutomationActionKind.SetNotifications, Toggle = false }];
                break;
            default:
                rule.Name = L.T("夜间模式"); rule.Trigger = AutomationTrigger.DailyTime; rule.Time = "23:00";
                rule.Actions = [new() { Kind = AutomationActionKind.SetVolume, Value = 20 }, new() { Kind = AutomationActionKind.SetNotifications, Toggle = false }];
                break;
        }
        Edit(rule);
    }
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected(sender) is not { } rule || controller is null) return;
        controller.DeleteAutomationRule(rule.Id);
        SetStatus(L.T("规则已删除。"));
    }
    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if (Selected(sender) is not { } rule || controller is null) return;
        var preview = controller.Automation.Preview(rule);
        var lines = new List<string> { L.T("检查仅预览条件与动作，不会执行。"), L.T(preview.Passed ? "当前条件满足" : "当前条件不满足") };
        lines.AddRange(preview.Errors);
        lines.AddRange(preview.Conditions.Select(condition => (condition.Passed ? "✓ " : "○ ") + condition.Description));
        lines.AddRange(rule.Actions.Select((action, index) => $"{index + 1}. {AutomationLabels.ActionSummary(controller, action)}"));
        SetStatus(string.Join("\n", lines));
    }
    private async void OnRun(object sender, RoutedEventArgs e) { if (Selected(sender) is { } rule && controller is not null) await Run(() => controller.Automation.ExecuteNowAsync(rule.Id)); }
    private async void OnRestore(object sender, RoutedEventArgs e) { if (Selected(sender) is { } rule && controller is not null) await Run(() => controller.Automation.RestoreRuleAsync(rule.Id)); }
    private async void OnRestoreAll(object sender, RoutedEventArgs e) { if (controller is not null) await Run(controller.Automation.RestoreAllAsync); }
    private async Task Run(Func<Task<string>> action)
    {
        try { SetStatus(await action()); }
        catch (Exception error) { SetStatus(L.F("操作未完成：{0}", error.Message)); }
        Refresh();
    }
    private void OnRefreshAudio(object sender, RoutedEventArgs e) { controller?.Automation.RefreshAudio(); Refresh(); }
    private void OnClearHistory(object sender, RoutedEventArgs e) { controller?.Automation.ClearHistory(); Refresh(); }
    private void SetStatus(string value) { status = value; StatusText.Text = value; }
    private sealed record Choice(string Value, string Label);
    private sealed record RuleRow(string Id, string Name, bool Enabled, string Summary, string Details, bool CanExecute, bool CanRestore);
    private sealed record HistoryRow(string Header, string Details);
}
