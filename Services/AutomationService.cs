using System.Text.Json;
using System.Windows.Threading;
using PowerHub.Models;

namespace PowerHub.Services;

public sealed class AutomationService : IDisposable
{
    private readonly AppSettings settings;
    private readonly Action<DesktopScene> applyDesktop;
    private readonly Func<DesktopScene> captureDesktop;
    private readonly Action<bool> setNotifications;
    private readonly Action<string> notify;
    private readonly IAutomationAudio audio;
    private readonly Func<IEnumerable<string>, IReadOnlySet<string>> readProcesses;
    private readonly bool preview;
    private readonly AutomationEngine engine = new();
    private readonly AutomationHistoryStore historyStore;
    private readonly List<AutomationLogEntry> history;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Queue<AutomationDecision> pending = new();
    private readonly Dictionary<string, RestorePoint> restorePoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> deferredRestores = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DeviceStatus> devices = [];
    private bool devicesAvailable;
    private bool started;
    private bool configuredEnabled;
    private string configuration = "";
    private bool disposed;
    private bool stopping;
    private long executionSequence;
    private CancellationTokenSource? executionCancellation;
    private Task<string>? operation;
    private Task? draining;
    public event Action? Changed;
    public IReadOnlyList<AutomationLogEntry> History => history;
    public bool IsExecuting { get; private set; }
    public int RestorePointCount => restorePoints.Count;
    public IReadOnlyList<AutomationAudioOutput> Outputs { get; private set; } = [];
    public string? AudioError { get; private set; }
    public bool HasMicrophone { get; private set; }
    public bool HasOutputControl { get; private set; }
    public string? ProcessError { get; private set; }
    public string? HistoryError => historyStore.Error;

    public AutomationService(AppSettings settings, string directory, Action<DesktopScene> applyDesktop,
        Func<DesktopScene> captureDesktop, Action<bool> setNotifications, Action<string> notify,
        bool preview = false, IAutomationAudio? audio = null,
        Func<IEnumerable<string>, IReadOnlySet<string>>? readProcesses = null)
    {
        this.settings = settings; this.applyDesktop = applyDesktop; this.captureDesktop = captureDesktop;
        this.setNotifications = setNotifications; this.notify = notify; this.preview = preview;
        this.audio = audio ?? new AutomationAudio();
        this.readProcesses = readProcesses ?? AutomationProcessMonitor.Read;
        historyStore = new AutomationHistoryStore(directory);
        history = historyStore.Load().ToList();
        historyStore.ErrorChanged += OnHistoryErrorChanged;
        timer.Tick += (_, _) => Observe();
    }

    private void OnHistoryErrorChanged()
    {
        if (!disposed && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(new Action(() => { if (!disposed) Changed?.Invoke(); }));
    }

    public void Start()
    {
        if (disposed || stopping || started || preview) return;
        started = true;
        ApplySettings();
    }

    public void ApplySettings()
    {
        if (disposed || stopping) return;
        var next = JsonSerializer.Serialize(settings.AutomationRules);
        var enabled = settings.AutomationEnabled;
        if (next != configuration || enabled != configuredEnabled)
        {
            configuration = next; configuredEnabled = enabled;
            foreach (var decision in pending.Where(decision => decision.Restore)) engine.ReleasePendingRestore(decision.Rule.Id);
            pending.Clear(); engine.Reset();
            if (!enabled) executionCancellation?.Cancel();
        }
        if (!started || preview || !enabled || !settings.AutomationRules.Any(rule => rule.Enabled)) timer.Stop();
        else if (!timer.IsEnabled) { Observe(); timer.Start(); }
        foreach (var point in restorePoints.Values.ToArray())
            if (!point.RequiresManualRetry && !settings.AutomationRules.Any(rule => rule.Id == point.Rule.Id && rule.Enabled))
                Enqueue(new(point.Rule, true, L.T("规则已删除或停用")));
    }

    public void ObserveDevices(ScanResult result)
    {
        if (disposed || stopping) return;
        devices = result.Devices.ToArray(); devicesAvailable = true;
        if (started) Observe();
    }

    public void DeviceScanFailed()
    {
        devicesAvailable = false; devices = [];
        if (started) Observe();
    }

    private IEnumerable<string> ProcessNames() => settings.AutomationRules.Where(rule => rule.Enabled)
        .SelectMany(rule => rule.Conditions.Where(condition => condition.Kind is AutomationConditionKind.ProcessRunning or AutomationConditionKind.ProcessNotRunning)
            .Select(condition => condition.ProcessName).Append(rule.Trigger is AutomationTrigger.ProcessStarted or AutomationTrigger.ProcessExited ? rule.ProcessName : ""))
        .Concat(restorePoints.Values.Select(point => point.Rule.Trigger is AutomationTrigger.ProcessStarted ? point.Rule.ProcessName : ""))
        .Where(name => !string.IsNullOrWhiteSpace(name));

    private AutomationSnapshot Snapshot(bool queryProcesses = true)
    {
        var snapshotDevices = devices.ToList();
        foreach (var alias in settings.DeviceLinks.Keys)
        {
            var id = settings.ResolveDeviceId(alias);
            if (devices.FirstOrDefault(device => device.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } linked &&
                !snapshotDevices.Any(device => device.Id.Equals(alias, StringComparison.OrdinalIgnoreCase))) snapshotDevices.Add(linked with { Id = alias });
        }
        if (!queryProcesses)
        {
            ProcessError = null;
            return new(DateTimeOffset.Now, snapshotDevices, new HashSet<string>(StringComparer.OrdinalIgnoreCase), devicesAvailable, false);
        }
        try
        {
            var processes = readProcesses(ProcessNames()); ProcessError = null;
            return new(DateTimeOffset.Now, snapshotDevices, processes, devicesAvailable, true);
        }
        catch (Exception error)
        {
            ProcessError = L.T("应用状态暂不可用：") + error.Message;
            return new(DateTimeOffset.Now, snapshotDevices, new HashSet<string>(StringComparer.OrdinalIgnoreCase), devicesAvailable, false);
        }
    }

    private void Observe()
    {
        if (disposed || stopping || preview) return;
        foreach (var decision in engine.Observe(Snapshot(settings.AutomationEnabled), settings.AutomationRules, settings.AutomationEnabled)) Enqueue(decision);
    }

    private void Enqueue(AutomationDecision decision)
    {
        if (disposed || stopping || preview ||
            pending.Any(item => item.Rule.Id == decision.Rule.Id && item.Restore == decision.Restore) ||
            decision.Restore && deferredRestores.Contains(decision.Rule.Id)) return;
        if (decision.Restore && restorePoints.TryGetValue(decision.Rule.Id, out var point) && point.RequiresManualRetry) return;
        if (!decision.Restore && pending.Count >= 64)
        { Log(decision.Rule, "未执行", decision.Reason, [L.T("待执行队列已满，请减少同时触发的规则。")]); return; }
        pending.Enqueue(decision with { Rule = decision.Rule.Clone() });
        if (draining is null || draining.IsCompleted) draining = DrainAsync();
    }

    private async Task DrainAsync()
    {
        await Task.Yield();
        while (!disposed && !stopping && pending.TryDequeue(out var decision))
        {
            if (IsExecuting) { pending.Enqueue(decision); return; }
            if (decision.Restore)
            {
                if (restorePoints.TryGetValue(decision.Rule.Id, out var point) && point.RequiresManualRetry) continue;
                operation = RestoreAsync(decision.Rule.Id, decision.Reason);
            }
            else
            {
                var rule = settings.AutomationRules.FirstOrDefault(rule => rule.Id == decision.Rule.Id && rule.Enabled);
                if (!settings.AutomationEnabled || rule is null || JsonSerializer.Serialize(rule) != JsonSerializer.Serialize(decision.Rule)) continue;
                operation = ExecuteAsync(rule.Clone(), decision.Reason);
            }
            await operation;
        }
    }

    public void RefreshAudio()
    {
        if (disposed) return;
        try
        {
            var state = audio.Read(); Outputs = state.Outputs; AudioError = state.Error;
            HasMicrophone = state.MicrophoneId is not null && state.MicrophoneMuted.HasValue;
            HasOutputControl = state.DefaultOutputId is not null && state.Volume.HasValue && state.OutputMuted.HasValue;
        }
        catch (Exception error) { Outputs = []; AudioError = error.Message; HasMicrophone = HasOutputControl = false; }
        Changed?.Invoke();
    }

    public IReadOnlyList<string> Validate(AutomationRule rule)
    {
        var errors = engine.Validate(rule).ToList();
        foreach (var action in rule.Actions)
        {
            if (action.Kind == AutomationActionKind.ApplyScene && !settings.DesktopScenes.Any(scene => scene.Id == action.TargetId))
                errors.Add(L.T("所选桌面场景已不存在，请重新选择。"));
            if (action.Kind == AutomationActionKind.RestorePrevious && action.TargetId.Length > 0 && action.TargetId != rule.Id &&
                !settings.AutomationRules.Any(target => target.Id == action.TargetId)) errors.Add(L.T("所选恢复规则已不存在。"));
        }
        if (rule.Actions.Any(action => IsAudio(action.Kind)))
        {
            RefreshAudio();
            foreach (var action in rule.Actions.Where(action => action.Kind == AutomationActionKind.SetAudioOutput))
                if (!Outputs.Any(output => output.Id == action.TargetId)) errors.Add(L.T("所选音频输出当前不可用，请刷新后选择。"));
            if (rule.Actions.Any(action => action.Kind is AutomationActionKind.SetVolume or AutomationActionKind.SetOutputMuted) && !HasOutputControl)
                errors.Add(L.T("当前默认音频输出无法控制，请检查系统声音设置。"));
            if (rule.Actions.Any(action => action.Kind == AutomationActionKind.SetMicrophoneMuted) && !HasMicrophone)
                errors.Add(L.T("当前通信麦克风不可用，请检查系统声音设置。"));
        }
        return errors.Distinct().ToArray();
    }

    public AutomationRulePreview Preview(AutomationRule rule)
    {
        var result = engine.Preview(rule, Snapshot());
        var errors = result.Errors.Concat(Validate(rule)).Distinct().ToList();
        foreach (var action in rule.Actions.Where(action => action.Kind == AutomationActionKind.RestorePrevious))
            if (!HasRestorePoint(action.TargetId.Length == 0 ? rule.Id : action.TargetId)) errors.Add(L.T("目标规则当前没有可恢复的执行记录。"));
        return result with { Passed = result.Passed && errors.Count == 0, Errors = errors };
    }

    public bool HasRestorePoint(string id) => restorePoints.ContainsKey(id);

    public Task<string> ExecuteNowAsync(string id)
    {
        if (disposed || stopping) return Task.FromResult(L.T("自动化中心已停止。"));
        if (IsExecuting) return Task.FromResult(L.T("正在执行其他动作，请稍后重试。"));
        if (preview) return Task.FromResult(L.T("预览模式只检查规则，不执行系统操作。"));
        if (!settings.AutomationEnabled) return Task.FromResult(L.T("自动化已暂停，请先开启总开关。"));
        var rule = settings.AutomationRules.FirstOrDefault(rule => rule.Id == id && rule.Enabled);
        if (rule is null) return Task.FromResult(L.T("规则不存在或已停用。"));
        operation = ExecuteAsync(rule.Clone(), L.T("手动执行"));
        return operation;
    }

    private async Task<string> ExecuteAsync(AutomationRule rule, string reason)
    {
        if (deferredRestores.Contains(rule.Id)) return L.T("此规则正在等待恢复，请先完成恢复。");
        var evaluation = Preview(rule);
        if (!evaluation.Passed)
        {
            var explanation = evaluation.Errors.Count > 0 ? string.Join(" ", evaluation.Errors)
                : L.T("当前条件不满足，未执行动作。");
            Log(rule, "未执行", reason, [explanation]);
            return explanation;
        }
        IsExecuting = true;
        executionCancellation?.Dispose();
        executionCancellation = new();
        var token = executionCancellation.Token;
        var steps = new List<string>();
        var status = "完成";
        Changed?.Invoke();
        try
        {
            audio.SetExecutionScope(rule.Id);
            foreach (var action in rule.Actions)
            {
                token.ThrowIfCancellationRequested();
                if (disposed || stopping || !settings.AutomationEnabled) throw new OperationCanceledException();
                if (action.Kind == AutomationActionKind.RestorePrevious)
                {
                    var target = action.TargetId.Length == 0 ? rule.Id : action.TargetId;
                    var outcome = RestorePointNow(target);
                    steps.Add(outcome.Message);
                    await RestoreDeferred();
                }
                else
                {
                    RestorePoint? point = null;
                    if (action.Kind != AutomationActionKind.Notify)
                    {
                        if (!restorePoints.TryGetValue(rule.Id, out point))
                        {
                            point = new(rule.Clone(), ++executionSequence, DesktopSnapshot(), settings.NotificationsEnabled);
                            restorePoints.Add(rule.Id, point);
                        }
                        if (IsAudio(action.Kind) && point.AudioBefore is null)
                            point.AudioBefore = point.AudioExpected = audio.Capture();
                        point.Changed.Add(action.Kind);
                    }
                    try
                    {
                        steps.Add(Perform(action));
                        if (action.Kind == AutomationActionKind.ApplyScene)
                            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    }
                    finally
                    {
                        if (point is not null)
                        {
                            if (action.Kind == AutomationActionKind.ApplyScene) point.ExpectedDesktop = DesktopSnapshot();
                            if (action.Kind == AutomationActionKind.SetNotifications) point.ExpectedNotifications = settings.NotificationsEnabled;
                            if (IsAudio(action.Kind)) point.AudioExpected = audio.Capture();
                        }
                    }
                }
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) { status = "已暂停"; steps.Add(L.T("后续动作已停止，已完成的动作可通过恢复功能还原。")); }
        catch (Exception error) { status = steps.Count > 0 ? "部分完成" : "失败"; steps.Add(error.Message); }
        finally
        {
            audio.SetExecutionScope(null);
            engine.MarkExecuted(rule, DateTimeOffset.Now, restorePoints.ContainsKey(rule.Id));
            IsExecuting = false;
            Log(rule, status, reason, steps);
            ContinueQueue();
        }
        return L.F("{0}：{1}", rule.Name, L.T(status));
    }

    private string Perform(AutomationAction action)
    {
        switch (action.Kind)
        {
            case AutomationActionKind.ApplyScene:
                var scene = settings.DesktopScenes.FirstOrDefault(scene => scene.Id == action.TargetId)
                    ?? throw new InvalidOperationException(L.T("所选桌面场景已不存在，请重新选择。"));
                applyDesktop(scene); return L.F("已应用场景：{0}", scene.Name);
            case AutomationActionKind.SetAudioOutput:
                audio.SetOutput(action.TargetId); return L.T("已切换音频输出");
            case AutomationActionKind.SetVolume:
                audio.SetVolume(action.Value / 100d); return L.F("已设置音量：{0}%", action.Value);
            case AutomationActionKind.SetOutputMuted:
                audio.SetOutputMuted(action.Toggle); return L.T(action.Toggle ? "已静音输出" : "已取消输出静音");
            case AutomationActionKind.SetMicrophoneMuted:
                audio.SetMicrophoneMuted(action.Toggle); return L.T(action.Toggle ? "已静音通信麦克风" : "已取消通信麦克风静音");
            case AutomationActionKind.SetNotifications:
                setNotifications(action.Toggle); return L.T(action.Toggle ? "已开启普通提醒" : "已暂停普通提醒");
            case AutomationActionKind.Notify:
                if (!settings.NotificationsEnabled) return L.T("普通提醒已关闭，跳过通知动作。");
                notify(action.Text); return L.T("已发送通知");
            default: throw new InvalidOperationException(L.T("不支持的自动化动作。"));
        }
    }

    private DesktopScene DesktopSnapshot()
    {
        var scene = captureDesktop(); scene.Id = ""; scene.Name = "";
        return scene;
    }
    private static bool SameDesktop(DesktopScene left, DesktopScene right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static bool IsAudio(AutomationActionKind kind) => kind is AutomationActionKind.SetAudioOutput or AutomationActionKind.SetVolume
        or AutomationActionKind.SetOutputMuted or AutomationActionKind.SetMicrophoneMuted;
    private static bool Overlaps(RestorePoint left, RestorePoint right) =>
        left.Changed.Contains(AutomationActionKind.ApplyScene) && right.Changed.Contains(AutomationActionKind.ApplyScene) ||
        left.Changed.Contains(AutomationActionKind.SetNotifications) && right.Changed.Contains(AutomationActionKind.SetNotifications) ||
        left.Changed.Any(IsAudio) && right.Changed.Any(IsAudio);

    private (string Message, string Status) RestorePointNow(string id)
    {
        if (!restorePoints.TryGetValue(id, out var point)) throw new InvalidOperationException(L.T("目标规则当前没有可恢复的执行记录。"));
        try { return RestorePointCore(id, point); }
        catch
        {
            point.RequiresManualRetry = true;
            deferredRestores.Remove(id);
            throw;
        }
    }

    private (string Message, string Status) RestorePointCore(string id, RestorePoint point)
    {
        if (restorePoints.Values.Any(other => other.Sequence > point.Sequence && Overlaps(point, other)))
        {
            deferredRestores.Add(id);
            return (L.T("恢复已排队，等待后续规则释放相同设置。"), "等待恢复");
        }
        var messages = new List<string>();
        if (point.Changed.Contains(AutomationActionKind.ApplyScene))
        {
            var current = DesktopSnapshot();
            if (!SameDesktop(current, point.BeforeDesktop))
            {
                if (SameDesktop(current, point.ExpectedDesktop)) applyDesktop(point.BeforeDesktop);
                else messages.Add(L.T("桌面已被手动修改，保留当前桌面。"));
            }
        }
        if (point.Changed.Contains(AutomationActionKind.SetNotifications))
        {
            if (settings.NotificationsEnabled == point.ExpectedNotifications) setNotifications(point.BeforeNotifications);
            else messages.Add(L.T("提醒开关已被手动修改，保留当前设置。"));
        }
        if (point.AudioBefore is not null && point.AudioExpected is not null)
        {
            var outcome = audio.Restore(point.AudioBefore, point.AudioExpected, point.Changed);
            messages.AddRange(outcome.Messages);
            if (outcome.RetryRequired)
            {
                point.RequiresManualRetry = true;
                deferredRestores.Remove(id);
                return (L.T("部分设置暂未恢复，恢复点已保留，可稍后重试。") + " " + string.Join(" ", messages), "部分恢复");
            }
        }
        restorePoints.Remove(id); deferredRestores.Remove(id); engine.ForgetActive(id);
        var restored = L.F("已恢复规则执行前的配置：{0}", point.Rule.Name);
        return messages.Count == 0 ? (restored, "已恢复") : (restored + " " + string.Join(" ", messages), "部分恢复");
    }

    public Task<string> RestoreRuleAsync(string id)
    {
        if (disposed || stopping) return Task.FromResult(L.T("自动化中心已停止。"));
        if (IsExecuting) return Task.FromResult(L.T("正在执行其他动作，请稍后重试。"));
        operation = RestoreAsync(id, L.T("手动恢复")); return operation;
    }
    private async Task<string> RestoreAsync(string id, string reason)
    {
        if (!restorePoints.TryGetValue(id, out var point)) return L.T("目标规则当前没有可恢复的执行记录。");
        IsExecuting = true; Changed?.Invoke();
        string message;
        try
        {
            var outcome = RestorePointNow(id);
            message = outcome.Message;
            Log(point.Rule, outcome.Status, reason, [message]);
            await RestoreDeferred();
        }
        catch (Exception error) { message = error.Message; Log(point.Rule, "恢复失败", reason, [message]); }
        finally { IsExecuting = false; Changed?.Invoke(); ContinueQueue(); }
        return message;
    }

    private async Task RestoreDeferred()
    {
        foreach (var id in deferredRestores.OrderByDescending(id => restorePoints.GetValueOrDefault(id)?.Sequence).ToArray())
        {
            if (!restorePoints.TryGetValue(id, out var point)) { deferredRestores.Remove(id); continue; }
            if (restorePoints.Values.Any(other => other.Sequence > point.Sequence && Overlaps(point, other))) continue;
            try
            {
                var outcome = RestorePointNow(id);
                Log(point.Rule, outcome.Status, L.T("后续规则已结束"), [outcome.Message]);
            }
            catch (Exception error) { Log(point.Rule, "恢复失败", L.T("后续规则已结束"), [error.Message]); }
            await Task.Yield();
        }
    }

    public async Task<string> RestoreAllAsync()
    {
        if (disposed || stopping) return L.T("自动化中心已停止。");
        if (IsExecuting) return L.T("正在执行其他动作，请稍后重试。");
        var count = restorePoints.Count;
        foreach (var id in restorePoints.Values.OrderByDescending(point => point.Sequence).Select(point => point.Rule.Id).ToArray())
            if (restorePoints.ContainsKey(id)) await RestoreRuleAsync(id);
        return L.F("已处理 {0} 个规则的恢复请求。", count);
    }

    private void ContinueQueue()
    {
        if (pending.Count > 0 && !disposed && !stopping)
            dispatcher.BeginInvoke(new Action(() => { if (!IsExecuting && (draining is null || draining.IsCompleted)) draining = DrainAsync(); }));
    }
    private void Log(AutomationRule rule, string status, string reason, IReadOnlyList<string> steps)
    {
        history.Insert(0, new(DateTimeOffset.Now, rule.Id, rule.Name, status, reason, steps.ToArray()));
        if (history.Count > 200) history.RemoveRange(200, history.Count - 200);
        if (!preview) historyStore.Save(history);
        Changed?.Invoke();
    }
    public void ClearHistory() { history.Clear(); if (!preview) historyStore.Save(history); Changed?.Invoke(); }

    public async Task StopAsync(CancellationToken token)
    {
        stopping = true; timer.Stop(); pending.Clear(); executionCancellation?.Cancel();
        if (operation is { IsCompleted: false }) await operation.WaitAsync(token);
        foreach (var point in restorePoints.Values.OrderByDescending(point => point.Sequence).ToArray())
        {
            token.ThrowIfCancellationRequested();
            try { var outcome = RestorePointNow(point.Rule.Id); Log(point.Rule, outcome.Status, L.T("退出自动化中心"), [outcome.Message]); }
            catch (Exception error) { Log(point.Rule, "恢复失败", L.T("退出自动化中心"), [error.Message]); }
        }
        await historyStore.FlushAsync(token);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); pending.Clear(); executionCancellation?.Cancel();
        historyStore.ErrorChanged -= OnHistoryErrorChanged;
        audio.Dispose(); executionCancellation?.Dispose();
    }

    private sealed class RestorePoint(AutomationRule rule, long sequence, DesktopScene desktop, bool notifications)
    {
        public AutomationRule Rule { get; } = rule;
        public long Sequence { get; } = sequence;
        public DesktopScene BeforeDesktop { get; } = desktop;
        public DesktopScene ExpectedDesktop { get; set; } = desktop;
        public bool BeforeNotifications { get; } = notifications;
        public bool ExpectedNotifications { get; set; } = notifications;
        public object? AudioBefore { get; set; }
        public object? AudioExpected { get; set; }
        public bool RequiresManualRetry { get; set; }
        public HashSet<AutomationActionKind> Changed { get; } = [];
    }
}
