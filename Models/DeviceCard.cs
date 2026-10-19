using System.Windows.Media;
using PowerHub.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace PowerHub.Models;

public sealed class DeviceCard(DeviceStatus status, AppSettings settings)
{
    public DeviceStatus Status { get; } = status;
    public string Id => Status.Id;
    public string DisplayStyle => settings.DisplayStyle;
    public bool AnimationsEnabled => settings.AnimationsEnabled && Status.ConnectionKnown && Status.IsConnected;
    public bool IsCharging => Status.IsCharging == true;
    public string Name => settings.GetDeviceName(Status);
    public Geometry IconGeometry => DeviceIcons.Get(Status.Category);
    public Brush NameBrush
    {
        get
        {
            if (settings.Devices.TryGetValue(Id, out var preference) && BatteryPalette.TryParseColor(preference.NameColor, out var color))
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            return System.Windows.Application.Current?.TryFindResource("TextPrimary") as Brush ?? Brushes.Black;
        }
    }
    public Brush BatteryBrush => BatteryPalette.GetBrush(Status.BatteryPercent);
    public string Subtitle => $"{Status.Connection} · {Status.ConnectionText}";
    public string Note => Status.IsCharging == true ? "正在充电，提醒已暂停" : !Status.HasBattery ? "暂未提供可读取的电量" : "电量低时会按你的设置提醒";
    public string PinLabel => settings.IsPinned(Id) ? "✓ 已固定" : "+ 固定桌面";
    public bool AlertsEnabled => !settings.Devices.TryGetValue(Id, out var preference) || preference.NotificationsEnabled;
    public string ThresholdOverride => settings.Devices.TryGetValue(Id, out var preference) ? preference.LowBatteryThreshold?.ToString() ?? "" : "";
    public string ReminderText => !settings.IsDeviceNotificationEnabled(Id) ? "提醒已关闭" : Status.BatteryPercent is null && Status.BatteryLevel is not null
        ? "低电量档位时提醒" : $"{settings.GetThreshold(Id)}% 时提醒";
}
