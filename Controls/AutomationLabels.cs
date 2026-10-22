using PowerHub.Models;
using PowerHub.Services;

namespace PowerHub.Controls;

public static class AutomationLabels
{
    public static string Trigger(AutomationTrigger value) => L.T(value switch
    {
        AutomationTrigger.DeviceConnected => "设备连接", AutomationTrigger.DeviceDisconnected => "设备断开",
        AutomationTrigger.BatteryBelow => "电量降至阈值", AutomationTrigger.ChargingStarted => "开始充电",
        AutomationTrigger.ChargingStopped => "停止充电", AutomationTrigger.ProcessStarted => "应用启动",
        AutomationTrigger.ProcessExited => "应用退出", AutomationTrigger.DailyTime => "每日时间", _ => "未知触发条件"
    });
    public static string Condition(AutomationConditionKind value) => L.T(value switch
    {
        AutomationConditionKind.DeviceConnected => "设备已连接", AutomationConditionKind.DeviceDisconnected => "设备已断开",
        AutomationConditionKind.BatteryAtMost => "电量不高于", AutomationConditionKind.Charging => "设备正在充电",
        AutomationConditionKind.NotCharging => "设备未在充电", AutomationConditionKind.ProcessRunning => "应用正在运行",
        AutomationConditionKind.ProcessNotRunning => "应用未在运行", AutomationConditionKind.TimeRange => "处于时间段", _ => "未知附加条件"
    });
    public static string Action(AutomationActionKind value) => L.T(value switch
    {
        AutomationActionKind.ApplyScene => "应用桌面场景", AutomationActionKind.SetAudioOutput => "切换音频输出",
        AutomationActionKind.SetVolume => "设置输出音量", AutomationActionKind.SetOutputMuted => "设置输出静音",
        AutomationActionKind.SetMicrophoneMuted => "设置通信麦克风静音", AutomationActionKind.SetNotifications => "设置全局提醒",
        AutomationActionKind.Notify => "显示通知", AutomationActionKind.RestorePrevious => "恢复之前配置", _ => "未知动作"
    });
    public static string DeviceName(AppController controller, string id)
    {
        var resolved = controller.Settings.ResolveDeviceId(id);
        var device = controller.Devices.FirstOrDefault(item => string.Equals(item.Id, resolved, StringComparison.OrdinalIgnoreCase));
        if (device is not null) return controller.Settings.GetDeviceName(device);
        if (controller.Settings.Devices.TryGetValue(resolved, out var saved)) return saved.CustomName ?? saved.DisplayName;
        return L.T("设备暂不可用");
    }
    public static string TriggerSummary(AppController controller, AutomationRule rule)
    {
        var detail = rule.Trigger switch
        {
            AutomationTrigger.ProcessStarted or AutomationTrigger.ProcessExited => rule.ProcessName,
            AutomationTrigger.DailyTime => rule.Time,
            _ => DeviceName(controller, rule.DeviceId)
        };
        if (rule.Trigger == AutomationTrigger.BatteryBelow) detail += $" · {rule.Threshold}%";
        return Trigger(rule.Trigger) + " · " + detail;
    }
    public static string ActionSummary(AppController controller, AutomationAction action)
    {
        var detail = action.Kind switch
        {
            AutomationActionKind.ApplyScene => controller.Settings.DesktopScenes.FirstOrDefault(scene => scene.Id == action.TargetId)?.Name ?? L.T("场景暂不可用"),
            AutomationActionKind.SetAudioOutput => controller.Automation.Outputs.FirstOrDefault(output => output.Id == action.TargetId)?.Name ?? L.T("音频输出暂不可用"),
            AutomationActionKind.SetVolume => $"{action.Value}%",
            AutomationActionKind.SetOutputMuted or AutomationActionKind.SetMicrophoneMuted or AutomationActionKind.SetNotifications => L.T(action.Toggle ? "开启" : "关闭"),
            AutomationActionKind.Notify => action.Text,
            AutomationActionKind.RestorePrevious => string.IsNullOrEmpty(action.TargetId) ? L.T("本规则")
                : controller.Settings.AutomationRules.FirstOrDefault(rule => rule.Id == action.TargetId)?.Name ?? L.T("规则暂不可用"),
            _ => ""
        };
        return Action(action.Kind) + (detail.Length > 0 ? " · " + detail : "");
    }
}
