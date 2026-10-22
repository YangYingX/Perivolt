using System.Globalization;
using PowerHub.Models;

namespace PowerHub.Services;

public sealed class AutomationEngine
{
    private sealed record DeviceMemory(bool? Connected, DeviceStatus? Battery, DeviceStatus? Charging);
    private readonly Dictionary<string, DeviceMemory> devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> knownRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lastExecuted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateOnly> dailyDates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AutomationRule> active = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pendingRestores = new(StringComparer.OrdinalIgnoreCase);
    private bool devicesInitialized;
    private bool processesInitialized;
    private bool clockInitialized;

    public IReadOnlyList<AutomationDecision> Observe(AutomationSnapshot snapshot, IReadOnlyList<AutomationRule> rules, bool enabled)
    {
        var currentRules = rules.Where(rule => rule is not null).GroupBy(rule => rule.Id ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).Take(AutomationRule.MaximumCount).ToArray();
        var currentDevices = DeviceMap(snapshot.Devices);
        var currentProcesses = snapshot.Processes.Select(AutomationRule.NormalizeProcess).Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decisions = new List<AutomationDecision>();

        if (enabled)
            foreach (var (id, rule) in active)
            {
                if (pendingRestores.Contains(id) || !HasEnded(rule, snapshot, currentDevices, currentProcesses)) continue;
                pendingRestores.Add(id);
                decisions.Add(new(rule.Clone(), true, L.T(rule.Trigger == AutomationTrigger.DeviceConnected
                    ? "设备断开，恢复之前配置" : "应用退出，恢复之前配置")));
            }

        foreach (var rule in currentRules)
        {
            if (Validate(rule).Count > 0) continue;
            var firstObservation = !knownRules.Contains(rule.Id);
            // Consume scheduled minutes even while paused, disabled, or on the first baseline.
            var scheduled = IsDailyDue(rule, snapshot.At);
            if (!enabled || !rule.Enabled || firstObservation || IsCoolingDown(rule, snapshot.At)) continue;
            var triggered = rule.Trigger == AutomationTrigger.DailyTime
                ? clockInitialized && scheduled
                : WasTriggered(rule, snapshot, currentDevices, currentProcesses);
            if (!triggered || !Preview(rule, snapshot).Passed) continue;
            decisions.Add(new(rule.Clone(), false, TriggerDescription(rule.Trigger)));
        }

        UpdateDeviceBaseline(snapshot, currentDevices, currentRules);
        if (snapshot.ProcessesAvailable)
        {
            processes.Clear();
            processes.UnionWith(currentProcesses);
            processesInitialized = true;
        }
        else { processes.Clear(); processesInitialized = false; }
        clockInitialized = true;
        knownRules.Clear();
        knownRules.UnionWith(currentRules.Where(rule => Guid.TryParseExact(rule.Id, "N", out _)).Select(rule => rule.Id));
        var retained = knownRules.Concat(active.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in lastExecuted.Keys.Where(id => !retained.Contains(id)).ToArray()) lastExecuted.Remove(id);
        foreach (var id in dailyDates.Keys.Where(id => !retained.Contains(id)).ToArray()) dailyDates.Remove(id);
        return decisions;
    }

    public AutomationRulePreview Preview(AutomationRule rule, AutomationSnapshot snapshot)
    {
        var errors = Validate(rule).ToList();
        if (!string.IsNullOrWhiteSpace(rule.Id) && IsCoolingDown(rule, snapshot.At)) errors.Add(L.T("规则仍在冷却中。"));
        var results = (rule.Conditions ?? []).Where(condition => condition is not null)
            .Select(condition => EvaluateCondition(condition, snapshot)).ToArray();
        var matched = results.Length == 0 || (rule.Match == AutomationMatch.All ? results.All(result => result.Passed) : results.Any(result => result.Passed));
        return new(errors.Count == 0 && matched, results, errors);
    }

    public IReadOnlyList<string> Validate(AutomationRule rule)
    {
        var errors = new List<string>();
        void Error(string text) { var translated = L.T(text); if (!errors.Contains(translated)) errors.Add(translated); }
        if (!Guid.TryParseExact(rule.Id, "N", out _)) Error("规则标识无效。");
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 80) Error("请输入不超过 80 字的规则名称。");
        if (!Enum.IsDefined(rule.Trigger)) Error("规则触发条件无效。");
        if (!Enum.IsDefined(rule.Match)) Error("规则条件组合方式无效。");
        if (rule.CooldownSeconds is < 0 or > 86400) Error("冷却时间须为 0–86400 秒。");
        if (IsDeviceTrigger(rule.Trigger) && string.IsNullOrWhiteSpace(rule.DeviceId)) Error("请选择触发设备。");
        if (rule.Trigger is AutomationTrigger.ProcessStarted or AutomationTrigger.ProcessExited && !ValidProcess(rule.ProcessName))
            Error("请输入应用进程名，不要填写路径。");
        if (rule.Trigger == AutomationTrigger.DailyTime && !TryTime(rule.Time, out _)) Error("每日时间须为 HH:mm。");
        if (rule.Trigger == AutomationTrigger.BatteryBelow && rule.Threshold is < 0 or > 100) Error("电量阈值须为 0–100。");
        if (rule.RestoreOnEnd && rule.Trigger is not (AutomationTrigger.DeviceConnected or AutomationTrigger.ProcessStarted))
            Error("结束时恢复仅适用于设备连接或应用启动。");
        if (rule.Conditions is null || rule.Conditions.Count > 8) Error("每条规则最多 8 个附加条件。");
        foreach (var condition in rule.Conditions ?? [])
        {
            if (condition is null || !Enum.IsDefined(condition.Kind)) { Error("规则包含无效的附加条件。"); continue; }
            if (IsDeviceCondition(condition.Kind) && string.IsNullOrWhiteSpace(condition.DeviceId)) Error("请选择条件设备。");
            if (condition.Kind is AutomationConditionKind.ProcessRunning or AutomationConditionKind.ProcessNotRunning && !ValidProcess(condition.ProcessName))
                Error("请输入应用进程名，不要填写路径。");
            if (condition.Kind == AutomationConditionKind.BatteryAtMost && condition.Threshold is < 0 or > 100) Error("电量阈值须为 0–100。");
            if (condition.Kind == AutomationConditionKind.TimeRange &&
                (!TryTime(condition.StartTime, out var start) || !TryTime(condition.EndTime, out var end) || start == end))
                Error("时间范围须为两个不同的 HH:mm。");
        }
        if (rule.Actions is null || rule.Actions.Count is < 1 or > 12) Error("请添加 1–12 个顺序动作。");
        foreach (var action in rule.Actions ?? [])
        {
            if (action is null || !Enum.IsDefined(action.Kind)) { Error("规则包含无效的动作。"); continue; }
            switch (action.Kind)
            {
                case AutomationActionKind.ApplyScene when !Guid.TryParseExact(action.TargetId, "N", out _): Error("请选择目标场景。"); break;
                case AutomationActionKind.SetAudioOutput when string.IsNullOrWhiteSpace(action.TargetId): Error("请选择音频输出。"); break;
                case AutomationActionKind.SetVolume when action.Value is < 0 or > 100: Error("音量须为 0–100。"); break;
                case AutomationActionKind.Notify when string.IsNullOrWhiteSpace(action.Text) || action.Text.Length > 240: Error("请输入不超过 240 字的提醒内容。"); break;
                case AutomationActionKind.RestorePrevious when !string.IsNullOrEmpty(action.TargetId) && !Guid.TryParseExact(action.TargetId, "N", out _): Error("恢复目标规则标识无效。"); break;
            }
        }
        return errors;
    }

    public void MarkExecuted(AutomationRule rule, DateTimeOffset at, bool restorable)
    {
        if (string.IsNullOrWhiteSpace(rule.Id)) return;
        lastExecuted[rule.Id] = at;
        if (restorable && rule.RestoreOnEnd && rule.Trigger is AutomationTrigger.DeviceConnected or AutomationTrigger.ProcessStarted)
        {
            active[rule.Id] = rule.Clone();
            pendingRestores.Remove(rule.Id);
        }
    }

    public void ForgetActive(string ruleId) { active.Remove(ruleId); pendingRestores.Remove(ruleId); }
    public void ReleasePendingRestore(string ruleId) => pendingRestores.Remove(ruleId);

    public void Reset()
    {
        devices.Clear(); processes.Clear();
        devicesInitialized = processesInitialized = clockInitialized = false;
    }

    private bool IsCoolingDown(AutomationRule rule, DateTimeOffset at) => lastExecuted.TryGetValue(rule.Id, out var last)
        && at - last < TimeSpan.FromSeconds(rule.CooldownSeconds);

    private bool IsDailyDue(AutomationRule rule, DateTimeOffset at)
    {
        if (rule.Trigger != AutomationTrigger.DailyTime || !TryTime(rule.Time, out var time)
            || at.Hour != time.Hour || at.Minute != time.Minute) return false;
        var date = DateOnly.FromDateTime(at.DateTime);
        if (dailyDates.TryGetValue(rule.Id, out var last) && date <= last) return false;
        dailyDates[rule.Id] = date;
        return true;
    }

    private bool WasTriggered(AutomationRule rule, AutomationSnapshot snapshot, Dictionary<string, DeviceStatus> current, HashSet<string> currentProcesses)
    {
        if (rule.Trigger is AutomationTrigger.ProcessStarted or AutomationTrigger.ProcessExited)
        {
            if (!snapshot.ProcessesAvailable || !processesInitialized) return false;
            var name = AutomationRule.NormalizeProcess(rule.ProcessName);
            return rule.Trigger == AutomationTrigger.ProcessStarted ? !processes.Contains(name) && currentProcesses.Contains(name)
                : processes.Contains(name) && !currentProcesses.Contains(name);
        }
        if (!snapshot.DevicesAvailable || !devicesInitialized || !IsDeviceTrigger(rule.Trigger)) return false;
        devices.TryGetValue(rule.DeviceId, out var previous);
        current.TryGetValue(rule.DeviceId, out var device);
        var connected = Connection(device);
        return rule.Trigger switch
        {
            AutomationTrigger.DeviceConnected => connected == true && previous?.Connected == false,
            AutomationTrigger.DeviceDisconnected => connected == false && previous?.Connected == true,
            AutomationTrigger.BatteryBelow => FreshBattery(device, snapshot.At) && device!.IsCharging != true &&
                FreshBattery(previous?.Battery, snapshot.At) && Newer(device, previous!.Battery!) &&
                previous.Battery!.BatteryPercent > rule.Threshold && device.BatteryPercent <= rule.Threshold,
            AutomationTrigger.ChargingStarted => FreshStatus(device, snapshot.At) && device!.IsCharging == true &&
                FreshStatus(previous?.Charging, snapshot.At) && Newer(device, previous!.Charging!) && previous.Charging!.IsCharging == false,
            AutomationTrigger.ChargingStopped => FreshStatus(device, snapshot.At) && device!.IsCharging == false &&
                FreshStatus(previous?.Charging, snapshot.At) && Newer(device, previous!.Charging!) && previous.Charging!.IsCharging == true,
            _ => false
        };
    }

    private static bool HasEnded(AutomationRule rule, AutomationSnapshot snapshot, Dictionary<string, DeviceStatus> current, HashSet<string> currentProcesses) => rule.Trigger switch
    {
        AutomationTrigger.DeviceConnected => snapshot.DevicesAvailable && Connection(current.GetValueOrDefault(rule.DeviceId)) == false,
        AutomationTrigger.ProcessStarted => snapshot.ProcessesAvailable && !currentProcesses.Contains(AutomationRule.NormalizeProcess(rule.ProcessName)),
        _ => false
    };

    private void UpdateDeviceBaseline(AutomationSnapshot snapshot, Dictionary<string, DeviceStatus> current, IReadOnlyList<AutomationRule> rules)
    {
        if (!snapshot.DevicesAvailable) { devices.Clear(); devicesInitialized = false; return; }
        var wanted = rules.Concat(active.Values).SelectMany(rule => new[] { rule.DeviceId }
            .Concat((rule.Conditions ?? []).Where(condition => condition is not null).Select(condition => condition.DeviceId)))
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in devices.Keys.Where(id => !wanted.Contains(id)).ToArray()) devices.Remove(id);
        foreach (var id in wanted)
        {
            devices.TryGetValue(id, out var previous);
            current.TryGetValue(id, out var device);
            var connected = Connection(device);
            if (connected == false) devices[id] = new(false, null, null);
            else if (connected is null) devices[id] = previous ?? new(null, null, null);
            else devices[id] = new(true,
                FreshBattery(device, snapshot.At) ? device : previous?.Battery,
                FreshStatus(device, snapshot.At) && device!.IsCharging.HasValue ? device : previous?.Charging);
        }
        devicesInitialized = true;
    }

    private static AutomationConditionResult EvaluateCondition(AutomationCondition condition, AutomationSnapshot snapshot)
    {
        var device = snapshot.Devices.FirstOrDefault(device => string.Equals(device.Id, condition.DeviceId, StringComparison.OrdinalIgnoreCase));
        var name = device?.Name ?? L.T("所选设备");
        var process = AutomationRule.NormalizeProcess(condition.ProcessName);
        var running = snapshot.Processes.Any(name => string.Equals(AutomationRule.NormalizeProcess(name), process, StringComparison.OrdinalIgnoreCase));
        var known = snapshot.DevicesAvailable && Connection(device) is not null;
        return condition.Kind switch
        {
            AutomationConditionKind.DeviceConnected => new(known && Connection(device) == true, L.F("{0} · 已连接", name)),
            AutomationConditionKind.DeviceDisconnected => new(known && Connection(device) == false, L.F("{0} · 已断开", name)),
            AutomationConditionKind.BatteryAtMost => new(snapshot.DevicesAvailable && FreshBattery(device, snapshot.At) && device!.BatteryPercent <= condition.Threshold,
                L.F("{0} · 电量不高于 {1}%", name, condition.Threshold)),
            AutomationConditionKind.Charging => new(snapshot.DevicesAvailable && FreshStatus(device, snapshot.At) && device!.IsCharging == true, L.F("{0} · 正在充电", name)),
            AutomationConditionKind.NotCharging => new(snapshot.DevicesAvailable && FreshStatus(device, snapshot.At) && device!.IsCharging == false, L.F("{0} · 未在充电", name)),
            AutomationConditionKind.ProcessRunning => new(snapshot.ProcessesAvailable && running, L.F("{0} · 正在运行", process)),
            AutomationConditionKind.ProcessNotRunning => new(snapshot.ProcessesAvailable && !running, L.F("{0} · 未在运行", process)),
            AutomationConditionKind.TimeRange => new(InTimeRange(snapshot.At, condition.StartTime, condition.EndTime), L.F("本地时间 {0}–{1}", condition.StartTime, condition.EndTime)),
            _ => new(false, L.T("规则包含无效的附加条件。"))
        };
    }

    private static Dictionary<string, DeviceStatus> DeviceMap(IReadOnlyList<DeviceStatus> source) => source.Where(device => !string.IsNullOrWhiteSpace(device.Id))
        .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
            group => group.FirstOrDefault(device => device.ConnectionKnown && device.IsConnected) ?? group.FirstOrDefault(device => device.ConnectionKnown) ?? group.First(),
            StringComparer.OrdinalIgnoreCase);
    private static bool? Connection(DeviceStatus? device) => device is null ? false : device.ConnectionKnown ? device.IsConnected : null;
    private static bool FreshStatus(DeviceStatus? device, DateTimeOffset at) => device is { ConnectionKnown: true, IsConnected: true, UpdatedAt: { } updated }
        && updated <= at && at - updated <= TimeSpan.FromMinutes(5)
        && !(device.Source ?? "").Contains("cache", StringComparison.OrdinalIgnoreCase) && !(device.Source ?? "").Contains("缓存", StringComparison.OrdinalIgnoreCase);
    private static bool FreshBattery(DeviceStatus? device, DateTimeOffset at) => FreshStatus(device, at) && device!.BatteryPercent is >= 0 and <= 100;
    private static bool Newer(DeviceStatus current, DeviceStatus previous) => current.UpdatedAt > previous.UpdatedAt;
    private static bool IsDeviceTrigger(AutomationTrigger trigger) => trigger is AutomationTrigger.DeviceConnected or AutomationTrigger.DeviceDisconnected
        or AutomationTrigger.BatteryBelow or AutomationTrigger.ChargingStarted or AutomationTrigger.ChargingStopped;
    private static bool IsDeviceCondition(AutomationConditionKind kind) => kind is AutomationConditionKind.DeviceConnected or AutomationConditionKind.DeviceDisconnected
        or AutomationConditionKind.BatteryAtMost or AutomationConditionKind.Charging or AutomationConditionKind.NotCharging;
    private static bool ValidProcess(string? value)
    {
        var name = AutomationRule.NormalizeProcess(value);
        return name.Length is > 0 and <= 160 && name is not ("." or "..") && name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0;
    }
    private static bool TryTime(string? value, out TimeOnly time) => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    private static bool InTimeRange(DateTimeOffset at, string startText, string endText)
    {
        if (!TryTime(startText, out var start) || !TryTime(endText, out var end) || start == end) return false;
        var now = TimeOnly.FromDateTime(at.DateTime);
        return start < end ? now >= start && now < end : now >= start || now < end;
    }
    private static string TriggerDescription(AutomationTrigger trigger) => L.T(trigger switch
    {
        AutomationTrigger.DeviceConnected => "设备已连接", AutomationTrigger.DeviceDisconnected => "设备已断开",
        AutomationTrigger.BatteryBelow => "电量跨过低电阈值", AutomationTrigger.ChargingStarted => "开始充电", AutomationTrigger.ChargingStopped => "停止充电",
        AutomationTrigger.ProcessStarted => "应用已启动", AutomationTrigger.ProcessExited => "应用已退出", AutomationTrigger.DailyTime => "到达每日时间", _ => "规则触发条件无效。"
    });
}
