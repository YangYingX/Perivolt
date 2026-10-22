namespace PowerHub.Models;

public enum AutomationTrigger { DeviceConnected, DeviceDisconnected, BatteryBelow, ChargingStarted, ChargingStopped, ProcessStarted, ProcessExited, DailyTime }
public enum AutomationConditionKind { DeviceConnected, DeviceDisconnected, BatteryAtMost, Charging, NotCharging, ProcessRunning, ProcessNotRunning, TimeRange }
public enum AutomationMatch { All, Any }
public enum AutomationActionKind { ApplyScene, SetAudioOutput, SetVolume, SetOutputMuted, SetMicrophoneMuted, SetNotifications, Notify, RestorePrevious }

public sealed class AutomationRule
{
    public const int MaximumCount = 32;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public AutomationTrigger Trigger { get; set; }
    public string DeviceId { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int Threshold { get; set; } = 20;
    public string Time { get; set; } = "23:00";
    public int CooldownSeconds { get; set; } = 60;
    public bool RestoreOnEnd { get; set; }
    public AutomationMatch Match { get; set; }
    public List<AutomationCondition> Conditions { get; set; } = [];
    public List<AutomationAction> Actions { get; set; } = [];

    public AutomationRule Clone() => new()
    {
        Id = Id, Name = Name, Enabled = Enabled, Trigger = Trigger, DeviceId = DeviceId, ProcessName = ProcessName,
        Threshold = Threshold, Time = Time, CooldownSeconds = CooldownSeconds, RestoreOnEnd = RestoreOnEnd, Match = Match,
        Conditions = Conditions.Select(condition => condition.Clone()).ToList(), Actions = Actions.Select(action => action.Clone()).ToList()
    };

    public void Normalize()
    {
        Name = Clean(Name, 80);
        DeviceId = Clean(DeviceId, 512);
        ProcessName = NormalizeProcess(ProcessName);
        Time = Clean(Time, 5);
        Threshold = Math.Clamp(Threshold, 0, 100);
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 86400);
        Conditions = (Conditions ?? []).Where(condition => condition is not null).Take(8).ToList();
        Actions = (Actions ?? []).Where(action => action is not null).Take(12).ToList();
        foreach (var condition in Conditions) condition.Normalize();
        foreach (var action in Actions) action.Normalize();
    }

    public static string NormalizeProcess(string? value)
    {
        var name = Clean(value, 160);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
    internal static string Clean(string? value, int maximum)
    {
        var text = (value ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= maximum ? text : text[..(char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum)];
    }
}

public sealed class AutomationCondition
{
    public AutomationConditionKind Kind { get; set; }
    public string DeviceId { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int Threshold { get; set; } = 20;
    public string StartTime { get; set; } = "22:00";
    public string EndTime { get; set; } = "07:00";
    public AutomationCondition Clone() => (AutomationCondition)MemberwiseClone();
    public void Normalize()
    {
        DeviceId = AutomationRule.Clean(DeviceId, 512); ProcessName = AutomationRule.NormalizeProcess(ProcessName);
        Threshold = Math.Clamp(Threshold, 0, 100); StartTime = AutomationRule.Clean(StartTime, 5); EndTime = AutomationRule.Clean(EndTime, 5);
    }
}

public sealed class AutomationAction
{
    public AutomationActionKind Kind { get; set; }
    public string TargetId { get; set; } = "";
    public int Value { get; set; } = 50;
    public bool Toggle { get; set; } = true;
    public string Text { get; set; } = "";
    public AutomationAction Clone() => (AutomationAction)MemberwiseClone();
    public void Normalize()
    {
        TargetId = AutomationRule.Clean(TargetId, 512); Value = Math.Clamp(Value, 0, 100); Text = AutomationRule.Clean(Text, 240);
    }
}

public sealed record AutomationLogEntry(DateTimeOffset At, string RuleId, string RuleName, string Status, string Reason, IReadOnlyList<string> Steps);
