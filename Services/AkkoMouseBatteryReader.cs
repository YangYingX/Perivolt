using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class AkkoMouseBatteryReader
{
    // Read-only 1A battery query from Akko's official mouse WebHID driver.
    // https://driver.akkogear.com/assets/driveGather-_wwh_1SR.js
    internal static bool Matches(DeviceStatus device, HidNative.Caps caps) =>
        device.VendorId == 0x38EE && device.ProductId == 0x000F
        && caps.UsagePage == 0xFF1C && caps.Usage == 0x0092
        && caps.InputReportByteLength == 64 && caps.OutputReportByteLength == 64
        && caps.FeatureReportByteLength == 0;

    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        if (!Matches(device, caps)) return null;
        var result = HidBatteryQuery.Read(path, caps, CreatePayload(), 4,
            payload => Parse(payload, device), out var status);
        detail = $"Akko mouse; VID={device.VendorId:X4}; PID={device.ProductId:X4}; {status}";
        return result ?? device with
        {
            Category = "鼠标", Connection = "2.4G 接收器",
            BatteryPercent = null, BatteryLevel = null, IsCharging = null, IsBatteryLow = null, UpdatedAt = null,
            Source = "Akko HID 电量查询", Message = $"已发现接收器；鼠标电量未知；{status}"
        };
    }

    internal static byte[] CreatePayload()
    {
        var payload = new byte[63];
        payload[2] = 0x1A; payload[3] = 6;
        return payload;
    }

    internal static DeviceStatus? Parse(ReadOnlySpan<byte> payload, DeviceStatus device)
    {
        if (payload.Length != 63 || payload[0] != 0 || payload[1] != 0
            || payload[2] != 0x1A || payload[3] != 6 || payload[7] > 100
            || payload.SequenceEqual(CreatePayload())) return null;
        return device with
        {
            Category = "鼠标", Connection = "2.4G 接收器",
            BatteryPercent = payload[7], BatteryLevel = null, IsBatteryLow = null,
            IsCharging = payload[8] <= 1 ? payload[8] == 1 : null,
            UpdatedAt = DateTimeOffset.Now, Source = "Akko HID 电量查询",
            Message = "已读取接收器报告的电量；鼠标无线在线状态未知"
        };
    }
}
