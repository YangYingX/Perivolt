using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PowerHub.Controls;
using PowerHub.Models;
using PowerHub.Services;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace PowerHub;

public sealed class AutomationRuleEditorWindow : Window
{
    private readonly AppController controller;
    private readonly AutomationRule draft;
    private readonly StackPanel body = new();
    private readonly StackPanel triggerParameters = new();
    private readonly StackPanel conditionRows = new();
    private readonly StackPanel actionRows = new();
    private readonly TextBlock errorText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 10) };
    private readonly TextBlock audioText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 12) };
    private readonly CheckBox restoreOnEnd;
    private readonly Button addCondition;
    private readonly Button addAction;

    public AutomationRuleEditorWindow(AppController appController, AutomationRule rule)
    {
        controller = appController;
        draft = rule.Clone();
        Title = L.T("编辑自动化规则");
        Width = 880; Height = 780; MinWidth = 740; MinHeight = 560;
        MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 30);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        SetResourceReference(BackgroundProperty, "PageBackground"); SetResourceReference(ForegroundProperty, "TextPrimary");
        var layout = new DockPanel { Margin = new Thickness(24, 18, 24, 22), LastChildFill = true };
        var footer = new StackPanel();
        errorText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        footer.Children.Add(new ScrollViewer { Content = errorText, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var commands = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        commands.Children.Add(Command("检查规则", (_, _) => Preview()));
        var cancel = Command("取消", (_, _) => DialogResult = false); cancel.IsCancel = true; commands.Children.Add(cancel);
        var save = Command("保存规则", (_, _) => Save()); save.SetResourceReference(StyleProperty, "AccentButton"); commands.Children.Add(save);
        footer.Children.Add(commands); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0) });
        Content = layout;

        Heading("规则设置");
        body.Children.Add(Field("规则名称", Text(draft, nameof(draft.Name), 80)));
        var general = new WrapPanel { Margin = new Thickness(0, 4, 0, 14) };
        var enabled = new CheckBox { Content = L.T("启用此规则"), Margin = new Thickness(0, 8, 24, 8) };
        enabled.SetBinding(CheckBox.IsCheckedProperty, Bind(draft, nameof(draft.Enabled))); general.Children.Add(enabled);
        general.Children.Add(Field("冷却时间（秒）", Integer(draft, nameof(draft.CooldownSeconds), 0, 86400), 170));
        body.Children.Add(general);

        Heading("何时触发");
        var trigger = EnumChoice(draft.Trigger, AutomationLabels.Trigger);
        trigger.SelectionChanged += (_, _) =>
        {
            if (trigger.SelectedValue is not AutomationTrigger kind) return;
            draft.Trigger = kind;
            RefreshTrigger();
        };
        body.Children.Add(Field("触发条件", trigger)); body.Children.Add(triggerParameters);
        restoreOnEnd = new CheckBox { Content = L.T("设备断开或应用退出后自动恢复之前配置"), Margin = new Thickness(0, 2, 0, 14) };
        restoreOnEnd.SetBinding(CheckBox.IsCheckedProperty, Bind(draft, nameof(draft.RestoreOnEnd)));
        body.Children.Add(restoreOnEnd);
        body.Children.Add(Caption("恢复只处理本规则改过的设置；手动更改过的值会保留。"));

        Heading("附加条件");
        var match = Choices(new[] { new Choice<AutomationMatch>(AutomationMatch.All, L.T("满足全部条件")), new Choice<AutomationMatch>(AutomationMatch.Any, L.T("满足任一条件")) }, draft.Match);
        match.SelectionChanged += (_, _) => { if (match.SelectedValue is AutomationMatch value) draft.Match = value; };
        body.Children.Add(Field("条件组合", match));
        body.Children.Add(Caption("不添加附加条件时，直接响应所选触发事件。时间段支持跨午夜。"));
        body.Children.Add(conditionRows);
        addCondition = Command("添加条件", (_, _) =>
        {
            if (draft.Conditions.Count >= 8) return;
            var condition = new AutomationCondition(); draft.Conditions.Add(condition); conditionRows.Children.Add(ConditionRow(condition)); UpdateLimits();
        });
        addCondition.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; body.Children.Add(addCondition);

        Heading("依次执行");
        body.Children.Add(Caption("动作从上到下执行；某一步失败时停止后续动作，并记录结果。"));
        var refresh = Command("刷新音频输出", (_, _) => RefreshAudio()); refresh.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; body.Children.Add(refresh);
        audioText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); body.Children.Add(audioText);
        body.Children.Add(actionRows);
        addAction = Command("添加动作", (_, _) =>
        {
            if (draft.Actions.Count >= 12) return;
            var action = new AutomationAction { Kind = AutomationActionKind.Notify }; draft.Actions.Add(action); actionRows.Children.Add(ActionRow(action)); UpdateLimits();
        });
        addAction.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; body.Children.Add(addAction);

        RefreshTrigger();
        foreach (var condition in draft.Conditions) conditionRows.Children.Add(ConditionRow(condition));
        foreach (var action in draft.Actions) actionRows.Children.Add(ActionRow(action));
        UpdateAudioText(); UpdateLimits();
    }

    private void RefreshTrigger()
    {
        triggerParameters.Children.Clear();
        if (draft.Trigger is AutomationTrigger.ProcessStarted or AutomationTrigger.ProcessExited)
            triggerParameters.Children.Add(Field("应用进程名", Text(draft, nameof(draft.ProcessName), 160, "例如：game.exe；只填写进程名，不填写路径。")));
        else if (draft.Trigger == AutomationTrigger.DailyTime)
            triggerParameters.Children.Add(Field("本地时间（HH:mm）", Text(draft, nameof(draft.Time), 5)));
        else
        {
            triggerParameters.Children.Add(Field("设备", DeviceChoice(draft.DeviceId, value => draft.DeviceId = value)));
            if (draft.Trigger == AutomationTrigger.BatteryBelow) triggerParameters.Children.Add(Field("电量阈值（%）", Integer(draft, nameof(draft.Threshold), 0, 100)));
        }
        if (restoreOnEnd is not null)
        {
            var available = draft.Trigger is AutomationTrigger.DeviceConnected or AutomationTrigger.ProcessStarted;
            restoreOnEnd.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            if (!available) { draft.RestoreOnEnd = false; restoreOnEnd.IsChecked = false; }
        }
    }

    private Border ConditionRow(AutomationCondition condition)
    {
        var content = new StackPanel();
        var row = Card(content);
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var kind = EnumChoice(condition.Kind, AutomationLabels.Condition);
        top.Children.Add(kind);
        var remove = Command("移除条件", (_, _) => { draft.Conditions.Remove(condition); conditionRows.Children.Remove(row); UpdateLimits(); });
        Grid.SetColumn(remove, 1); top.Children.Add(remove); content.Children.Add(top);
        var parameters = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; content.Children.Add(parameters);
        void RefreshParameters()
        {
            parameters.Children.Clear();
            if (condition.Kind is AutomationConditionKind.ProcessRunning or AutomationConditionKind.ProcessNotRunning)
                parameters.Children.Add(Field("应用进程名", Text(condition, nameof(condition.ProcessName), 160, "例如：game.exe；只填写进程名，不填写路径。")));
            else if (condition.Kind == AutomationConditionKind.TimeRange)
            {
                var times = new Grid(); times.ColumnDefinitions.Add(new ColumnDefinition()); times.ColumnDefinitions.Add(new ColumnDefinition());
                var start = Field("开始时间（HH:mm）", Text(condition, nameof(condition.StartTime), 5)); start.Margin = new Thickness(0, 0, 12, 0);
                var end = Field("结束时间（HH:mm）", Text(condition, nameof(condition.EndTime), 5)); Grid.SetColumn(end, 1);
                times.Children.Add(start); times.Children.Add(end); parameters.Children.Add(times);
            }
            else
            {
                parameters.Children.Add(Field("设备", DeviceChoice(condition.DeviceId, value => condition.DeviceId = value)));
                if (condition.Kind == AutomationConditionKind.BatteryAtMost) parameters.Children.Add(Field("电量阈值（%）", Integer(condition, nameof(condition.Threshold), 0, 100)));
            }
        }
        kind.SelectionChanged += (_, _) => { if (kind.SelectedValue is AutomationConditionKind value) { condition.Kind = value; RefreshParameters(); } };
        RefreshParameters(); return row;
    }

    private Border ActionRow(AutomationAction action)
    {
        var content = new StackPanel(); var row = Card(content);
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var kind = Choices(Enum.GetValues<AutomationActionKind>().Select(value => new Choice<AutomationActionKind>(value, AutomationLabels.Action(value), ActionAvailable(value))), action.Kind);
        top.Children.Add(kind);
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(Command("上移", (_, _) => MoveAction(action, row, -1)));
        buttons.Children.Add(Command("下移", (_, _) => MoveAction(action, row, 1)));
        buttons.Children.Add(Command("移除动作", (_, _) => { draft.Actions.Remove(action); actionRows.Children.Remove(row); UpdateLimits(); }));
        Grid.SetColumn(buttons, 1); top.Children.Add(buttons); content.Children.Add(top);
        var parameters = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; content.Children.Add(parameters);
        void RefreshParameters()
        {
            parameters.Children.Clear();
            switch (action.Kind)
            {
                case AutomationActionKind.ApplyScene:
                    parameters.Children.Add(Field("桌面场景", TargetChoice(controller.Settings.DesktopScenes.Select(scene => new Choice<string>(scene.Id, scene.Name)), action.TargetId, "请选择桌面场景", "场景暂不可用", value => action.TargetId = value)));
                    if (controller.Settings.DesktopScenes.Count == 0) parameters.Children.Add(Caption("请先在桌面卡片设置中保存一个场景。"));
                    break;
                case AutomationActionKind.SetAudioOutput:
                    var output = TargetChoice(controller.Automation.Outputs.Select(item => new Choice<string>(item.Id, item.Name)), action.TargetId, "请选择音频输出", "音频输出暂不可用", value => action.TargetId = value);
                    output.IsEnabled = controller.Automation.Outputs.Count > 0; parameters.Children.Add(Field("音频输出", output)); break;
                case AutomationActionKind.SetVolume:
                    parameters.Children.Add(Field("音量（%）", Integer(action, nameof(action.Value), 0, 100))); break;
                case AutomationActionKind.SetOutputMuted:
                case AutomationActionKind.SetMicrophoneMuted:
                case AutomationActionKind.SetNotifications:
                    var toggle = new CheckBox { Content = L.T(action.Kind == AutomationActionKind.SetNotifications ? "开启全局提醒" : "开启静音"), Margin = new Thickness(0, 0, 0, 10) };
                    toggle.SetBinding(CheckBox.IsCheckedProperty, Bind(action, nameof(action.Toggle))); parameters.Children.Add(toggle); break;
                case AutomationActionKind.Notify:
                    parameters.Children.Add(Field("通知内容", Text(action, nameof(action.Text), 240))); break;
                case AutomationActionKind.RestorePrevious:
                    if (action.TargetId == draft.Id) action.TargetId = "";
                    var restoreChoices = new[] { new Choice<string>("", L.T("本规则")) }.Concat(controller.Settings.AutomationRules.Where(rule => rule.Id != draft.Id).Select(rule => new Choice<string>(rule.Id, rule.Name))).ToList();
                    if (action.TargetId.Length > 0 && !restoreChoices.Any(item => item.Value == action.TargetId)) restoreChoices.Add(new(action.TargetId, L.T("规则暂不可用"), false));
                    var restore = Choices(restoreChoices, action.TargetId);
                    restore.SelectionChanged += (_, _) => { if (restore.SelectedValue is string id) action.TargetId = id; };
                    parameters.Children.Add(Field("恢复哪条规则之前的配置", restore));
                    parameters.Children.Add(Caption("应用退出规则可选择对应的应用启动规则，恢复它执行前的配置。")); break;
            }
            if (!ActionAvailable(action.Kind)) parameters.Children.Add(Caption("此音频端点暂不可用，请连接设备后刷新。"));
        }
        kind.SelectionChanged += (_, _) => { if (kind.SelectedValue is AutomationActionKind value) { action.Kind = value; action.TargetId = ""; RefreshParameters(); } };
        RefreshParameters(); return row;
    }

    private bool ActionAvailable(AutomationActionKind kind) => kind switch
    {
        AutomationActionKind.SetAudioOutput => controller.Automation.Outputs.Count > 0,
        AutomationActionKind.SetVolume or AutomationActionKind.SetOutputMuted => controller.Automation.HasOutputControl,
        AutomationActionKind.SetMicrophoneMuted => controller.Automation.HasMicrophone,
        _ => true
    };
    private void MoveAction(AutomationAction action, UIElement row, int delta)
    {
        var index = draft.Actions.IndexOf(action); var next = index + delta;
        if (index < 0 || next < 0 || next >= draft.Actions.Count) return;
        draft.Actions.RemoveAt(index); draft.Actions.Insert(next, action);
        actionRows.Children.Remove(row); actionRows.Children.Insert(next, row);
    }
    private void RefreshAudio()
    {
        if (HasErrors(body)) { ShowErrors([L.T("请先修正数字输入。")]); return; }
        controller.Automation.RefreshAudio();
        actionRows.Children.Clear(); foreach (var action in draft.Actions) actionRows.Children.Add(ActionRow(action)); UpdateAudioText();
    }
    private void UpdateAudioText() => audioText.Text = controller.Automation.AudioError ?? (controller.Automation.Outputs.Count == 0 ? L.T("无可用音频输出。") : L.T("音频动作使用当前默认输出或所选输出；不访问麦克风音频流。"));
    private void UpdateLimits() { addCondition.IsEnabled = draft.Conditions.Count < 8; addAction.IsEnabled = draft.Actions.Count < 12; }
    private void Save()
    {
        if (HasErrors(body)) { ShowErrors([L.T("请先修正数字输入。")]); return; }
        var errors = controller.SaveAutomationRule(draft);
        if (errors.Count > 0) { ShowErrors(errors); return; }
        DialogResult = true;
    }
    private void Preview()
    {
        if (HasErrors(body)) { ShowErrors([L.T("请先修正数字输入。")]); return; }
        var result = controller.Automation.Preview(draft);
        ShowErrors(new[] { L.T("检查仅预览条件与动作，不会执行。"), L.T(result.Passed ? "当前条件满足" : "当前条件不满足") }
            .Concat(result.Errors).Concat(result.Conditions.Select(item => (item.Passed ? "✓ " : "○ ") + item.Description))
            .Concat(draft.Actions.Select((action, index) => $"{index + 1}. {AutomationLabels.ActionSummary(controller, action)}")));
    }
    private void ShowErrors(IEnumerable<string> errors) => errorText.Text = string.Join("\n", errors);
    private ComboBox DeviceChoice(string selected, Action<string> changed)
    {
        var devices = controller.Settings.Devices.Where(pair => controller.Settings.ResolveDeviceId(pair.Key) == pair.Key)
            .Select(pair => new Choice<string>(pair.Key, string.IsNullOrWhiteSpace(pair.Value.CustomName ?? pair.Value.DisplayName) ? L.T("未命名设备") : pair.Value.CustomName ?? pair.Value.DisplayName)).ToList();
        foreach (var device in controller.Devices)
            if (!devices.Any(item => string.Equals(item.Value, device.Id, StringComparison.OrdinalIgnoreCase))) devices.Add(new(device.Id, controller.Settings.GetDeviceName(device)));
        return TargetChoice(devices.OrderBy(item => item.Label), selected, "请选择设备", "设备暂不可用", changed);
    }
    private static ComboBox TargetChoice(IEnumerable<Choice<string>> source, string selected, string empty, string unavailable, Action<string> changed)
    {
        var items = source.ToList();
        if (selected.Length > 0 && !items.Any(item => item.Value == selected)) items.Add(new(selected, L.T(unavailable), false));
        items.Insert(0, new("", L.T(empty)));
        var combo = Choices(items, selected);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is string value) changed(value); };
        return combo;
    }
    private static ComboBox EnumChoice<T>(T selected, Func<T, string> label) where T : struct, Enum => Choices(Enum.GetValues<T>().Select(value => new Choice<T>(value, label(value))), selected);
    private static ComboBox Choices<T>(IEnumerable<Choice<T>> choices, T selected)
    {
        var combo = new ComboBox { ItemsSource = choices.ToArray(), DisplayMemberPath = nameof(Choice<T>.Label), SelectedValuePath = nameof(Choice<T>.Value), MinHeight = 36 };
        var itemStyle = new Style(typeof(ComboBoxItem)); itemStyle.Setters.Add(new Setter(IsEnabledProperty, new Binding(nameof(Choice<T>.Available)))); combo.ItemContainerStyle = itemStyle;
        combo.SelectedValue = selected; return combo;
    }
    private void Heading(string key) => body.Children.Add(new TextBlock { Text = L.T(key), FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 22, 0, 14) });
    private static Border Card(UIElement content)
    {
        var border = new Border { Child = content, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 12) };
        border.SetResourceReference(Border.BackgroundProperty, "Surface"); border.SetResourceReference(Border.BorderBrushProperty, "Stroke"); return border;
    }
    private static TextBlock Caption(string key)
    {
        var text = new TextBlock { Text = L.T(key), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), LineHeight = 18 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); return text;
    }
    private static StackPanel Field(string key, FrameworkElement input, double? width = null)
    {
        var field = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; if (width.HasValue) field.Width = width.Value;
        var label = new TextBlock { Text = L.T(key), FontSize = 11, Margin = new Thickness(0, 0, 0, 6) }; label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        field.Children.Add(label); field.Children.Add(input); System.Windows.Automation.AutomationProperties.SetName(input, L.T(key)); return field;
    }
    private static TextBox Text(object source, string property, int maximum, string? hint = null)
    {
        var box = new TextBox { MaxLength = maximum, MinHeight = 36, Padding = new Thickness(10, 7, 10, 7) }; box.SetBinding(TextBox.TextProperty, Bind(source, property));
        if (hint is not null) box.ToolTip = L.T(hint); return box;
    }
    private static TextBox Integer(object source, string property, int minimum, int maximum)
    {
        var box = new TextBox { MaxLength = 6, MinHeight = 36, Padding = new Thickness(10, 7, 10, 7), ToolTip = L.F("请输入 {0}–{1} 的整数。", minimum, maximum) };
        var binding = Bind(source, property); binding.ValidationRules.Add(new IntegerRange(minimum, maximum)); box.SetBinding(TextBox.TextProperty, binding); return box;
    }
    private static Binding Bind(object source, string property) => new(property) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true };
    private static Button Command(string key, RoutedEventHandler handler)
    {
        var button = new Button { Content = L.T(key), Padding = new Thickness(11, 7, 11, 7), FontSize = 11, Margin = new Thickness(7, 0, 0, 0), MinHeight = 34 }; button.Click += handler; return button;
    }
    private static bool HasErrors(DependencyObject root)
    {
        if (Validation.GetHasError(root)) return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) if (HasErrors(VisualTreeHelper.GetChild(root, index))) return true;
        return false;
    }
    private sealed record Choice<T>(T Value, string Label, bool Available = true);
    private sealed class IntegerRange(int minimum, int maximum) : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo) => int.TryParse(value?.ToString(), NumberStyles.Integer, cultureInfo, out var number) && number >= minimum && number <= maximum
            ? ValidationResult.ValidResult : new ValidationResult(false, L.F("请输入 {0}–{1} 的整数。", minimum, maximum));
    }
}
