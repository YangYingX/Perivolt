using System.Runtime.InteropServices;
using PowerHub.Models;

namespace PowerHub.Services;

public sealed class XInputBatteryProvider
{
    public IReadOnlyList<DeviceStatus> Scan(List<string> diagnostics)
    {
        var devices = new List<DeviceStatus>();
        try
        {
            for (uint index = 0; index < 4; index++)
            {
                if (XInputGetBatteryInformation(index, 0, out var battery) != 0 || battery.Type == 0) continue;
                var level = battery.Type is 2 or 3 ? battery.Level switch
                {
                    0 => "已耗尽", 1 => "低电量", 2 => "中等", 3 => "充足", _ => null
                } : null;
                devices.Add(new DeviceStatus
                {
                    Id = $"xinput:{index}", Name = $"XInput 手柄 {index + 1}", Category = "手柄",
                    Connection = battery.Type == 1 ? "USB（XInput）" : "无线（XInput）",
                    IsConnected = true, BatteryLevel = level, Source = "Windows XInput",
                    IsBatteryLow = level is not null ? battery.Level <= 1 : null,
                    PowerSource = battery.Type == 1 ? "USB 供电" : null,
                    UpdatedAt = level is null ? null : DateTimeOffset.Now,
                    Message = level is not null ? "XInput 仅报告电量档位，未转换成百分比"
                        : "XInput 未提供可用电池信息；有线状态不代表正在充电"
                });
            }
            diagnostics.Add($"XInput：发现 {devices.Count} 个手柄");
        }
        catch (DllNotFoundException) { diagnostics.Add("当前系统没有 XInput 1.4，跳过手柄读取"); }
        return devices;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Battery { public byte Type; public byte Level; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint index, byte deviceType, out Battery battery);
}
