using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class LangtuLt95BatteryReader
{
    // LT95 / LT95Pro official driver 1.0.1.0: MI_03, unnumbered 20 01 battery query.
    // This family is separate from LANGTU's Shinetek keyboards; see docs/protocol-families.md.
    internal static bool Matches(DeviceStatus device, HidNative.Caps caps) =>
        device.VendorId == 0x05AC && device.ProductId == 0x024F
        && string.Equals(device.Name, "LT95PRO-2.4G", StringComparison.OrdinalIgnoreCase)
        && device.InstanceId?.Contains("&MI_03", StringComparison.OrdinalIgnoreCase) == true
        && caps.UsagePage == 0xFF60 && caps.Usage == 0x0061
        && caps.InputReportByteLength is 33 or 65 && caps.OutputReportByteLength is 33 or 65;

    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        if (!Matches(device, caps)) return null;
        var reading = HidBatteryQuery.Read(path, caps, CreatePayload(caps.OutputReportByteLength - 1), 0,
            payload => Parse(payload, device), out var status);
        detail = $"LANGTU LT95; VID={device.VendorId:X4}; PID={device.ProductId:X4}; {status}";
        return reading ?? device with
        {
            Category = "键盘", Connection = "2.4G 接收器",
            BatteryPercent = null, BatteryLevel = null, IsCharging = null, IsBatteryLow = null, UpdatedAt = null,
            Source = "LANGTU LT95 电量协议", Message = status
        };
    }

    internal static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        payload[0] = 0x20; payload[1] = 0x01;
        // The checksum stays at Windows byte 32 even when the descriptor requires padding.
        payload[31] = 0x21;
        return payload;
    }

    internal static DeviceStatus? Parse(ReadOnlySpan<byte> payload, DeviceStatus device)
    {
        if (payload.Length is not (32 or 64) || payload[0] != 0x20 || payload[1] != 0x01 || payload[2] != 0
            || payload[3] is 0 or > 100) return null;
        return device with
        {
            // Retain Windows' receiver connection state; the packet has no wireless-online field.
            Category = "键盘", Connection = "2.4G 接收器",
            BatteryPercent = payload[3], BatteryLevel = null, IsCharging = null, IsBatteryLow = null,
            UpdatedAt = DateTimeOffset.Now, Source = "LANGTU LT95 电量协议",
            Message = "接收器已返回电量；协议未提供独立在线和充电状态"
        };
    }
}
