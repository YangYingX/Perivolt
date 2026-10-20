using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class LangtuKeyboardBatteryReader
{
    // Shinetek protocol 1, shared by the official LANGTU multi-keyboard driver.
    // Read-only status: AA 55 CC 33 0E. The receiver reports the actual paired PID.
    internal static string? KeyboardName(int? product) => product switch
    {
        0xA116 => "LANGTU L102",
        0xB002 or 0xB807 => "LANGTU L99",
        0xA071 => "LANGTU T75",
        0xA047 => "LANGTU T98",
        0xA137 => "LANGTU LT84 PRO",
        _ => null
    };

    internal static bool Matches(DeviceStatus device, HidNative.Caps caps) =>
        device.VendorId == 0x1A2C && (device.ProductId == 0x8FFF || KeyboardName(device.ProductId) is not null)
        && caps.UsagePage == 0xFF02 && caps.Usage == 2
        && caps.InputReportByteLength is 33 or 65 && caps.OutputReportByteLength is 33 or 65;

    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        if (!Matches(device, caps)) return null;
        var result = HidBatteryQuery.Read(path, caps, CreatePayload(caps.OutputReportByteLength - 1), null,
            payload => Parse(payload, device), out var status);
        detail = $"LANGTU Shinetek; VID={device.VendorId:X4}; PID={device.ProductId:X4}; {status}";
        return result ?? device with
        {
            Category = "键盘", Connection = device.ProductId == 0x8FFF ? "2.4G" : "USB 有线",
            ConnectionKnown = false, IsConnected = false, BatteryPercent = null, BatteryLevel = null,
            IsCharging = null, IsBatteryLow = null, UpdatedAt = null, Source = "LANGTU 键盘系列电量协议", Message = status
        };
    }

    internal static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        payload[0] = 0xAA; payload[1] = 0x55; payload[2] = 0xCC; payload[3] = 0x33; payload[4] = 0x0E;
        return payload;
    }

    internal static DeviceStatus? Parse(ReadOnlySpan<byte> payload, DeviceStatus device)
    {
        if (payload.Length is not (32 or 64) || !payload[..5].SequenceEqual(new byte[] { 0xAA, 0x55, 0xCC, 0x33, 0x0E })
            || payload.SequenceEqual(CreatePayload(payload.Length))) return null;
        var vendor = payload[8] << 8 | payload[9];
        var product = payload[10] << 8 | payload[11];
        var name = KeyboardName(product);
        // Shared dongle IDs can also belong to other OEM keyboards. Require the paired identity.
        if (vendor != 0x1A2C || name is null || device.ProductId != 0x8FFF && product != device.ProductId) return null;
        if (payload[6] is not (0 or 1 or 3)) return null;
        var receiver = device.ProductId == 0x8FFF;
        var online = receiver ? payload[6] == 1 : payload[6] == 3;
        if (online && payload[5] > 100) return null;
        return device with
        {
            Name = name, Category = "键盘", Connection = receiver ? "2.4G" : "USB 有线",
            ConnectionKnown = true, IsConnected = online, BatteryPercent = online ? payload[5] : null,
            BatteryLevel = null, IsCharging = online && payload[7] <= 1 ? payload[7] == 1 : null, IsBatteryLow = null,
            UpdatedAt = online ? DateTimeOffset.Now : null, Source = "LANGTU 键盘系列电量协议",
            Message = online ? $"已确认 {name} 在线并读取电量" : "接收器报告外设离线或已切换连接模式，已清除电量"
        };
    }
}
