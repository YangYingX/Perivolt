using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class RazerBatteryReader
{
    // Protocol/PID reference: BatteryMonitor, Copyright (c) 2026 JKWTCN (MIT).
    // https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/RazerHidProvider.cpp
    private readonly record struct Protocol(byte Transaction, int Delay, bool Charging, bool Wired, bool Keyboard = false);

    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        var protocol = Configuration(device.ProductId);
        if (device.VendorId != 0x1532 || protocol is not { } config || caps.UsagePage < 0xFF00 || caps.FeatureReportByteLength != 91)
            return null;

        var prefix = $"Razer VID={device.VendorId:X4}; PID={device.ProductId:X4}";
        using var handle = HidNative.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        var failure = "";
        if (handle.IsInvalid) failure = $"无法打开厂商电量接口，Windows 错误 {Marshal.GetLastWin32Error()}";
        else if (Query(handle, config, 0x80, out var raw, out failure))
        {
            bool? charging = null;
            if (config.Charging)
            {
                if (Query(handle, config, 0x84, out var flag, out var chargingFailure))
                {
                    if (flag <= 1) charging = flag == 1;
                    else failure = "充电字段超出有效范围";
                }
                else failure = chargingFailure;
            }

            var percent = (raw * 100 + 127) / 255;
            detail = $"{prefix}; battery={percent}%; charging={charging?.ToString() ?? "unknown"}; {failure}";
            return device with
            {
                Category = config.Keyboard ? "键盘" : "鼠标", Manufacturer = "Razer",
                Connection = config.Wired ? "USB 有线" : "2.4G", ConnectionKnown = true, IsConnected = true,
                BatteryPercent = percent, BatteryLevel = null, IsCharging = charging, UpdatedAt = DateTimeOffset.Now,
                Source = "Razer HID 电量查询", Message = charging.HasValue ? "已读取厂商电量与充电报告" : "已读取厂商电量；充电状态未知"
            };
        }

        detail = $"{prefix}; {failure}";
        return device with
        {
            Category = config.Keyboard ? "键盘" : "鼠标", BatteryPercent = null, BatteryLevel = null,
            IsCharging = null, UpdatedAt = null, Source = "Razer HID 电量查询", Message = failure,
            ConnectionKnown = config.Wired && device.ConnectionKnown, IsConnected = config.Wired && device.IsConnected
        };
    }

    private static bool Query(SafeFileHandle handle, Protocol config, byte command, out byte value, out string failure)
    {
        value = 0;
        var request = CreateRequest(config.Transaction, command);
        if (!HidD_SetFeature(handle, request, (uint)request.Length))
        {
            failure = $"电量查询发送失败，Windows 错误 {Marshal.GetLastWin32Error()}";
            return false;
        }
        Thread.Sleep(config.Delay);
        var response = new byte[91];
        if (!HidNative.HidD_GetFeature(handle, response, (uint)response.Length))
        {
            failure = $"厂商报告读取失败，Windows 错误 {Marshal.GetLastWin32Error()}";
            return false;
        }
        failure = TryParseResponse(response, config.Transaction, command, out value) ? "" : "厂商报告状态、命令或校验和不匹配；未确认无线外设在线";
        return failure.Length == 0;
    }

    internal static byte[] CreateRequest(byte transaction, byte command)
    {
        // Windows prepends report ID 0 to the standard 90-byte Razer packet.
        var report = new byte[91];
        report[2] = transaction;
        report[6] = 2;
        report[7] = 7;
        report[8] = command;
        report[89] = Checksum(report);
        return report;
    }

    internal static bool TryParseResponse(ReadOnlySpan<byte> report, byte transaction, byte command, out byte value)
    {
        value = 0;
        if (report.Length != 91 || report[0] != 0 || report[1] != 2 || report[2] != transaction ||
            report[3] != 0 || report[4] != 0 || report[5] != 0 || report[6] != 2 || report[7] != 7 ||
            report[8] != command || report[89] != Checksum(report)) return false;
        value = report[10];
        return true;
    }

    private static byte Checksum(ReadOnlySpan<byte> report)
    {
        byte checksum = 0;
        for (var index = 3; index < 89; index++) checksum ^= report[index];
        return checksum;
    }

    private static Protocol? Configuration(int? product) => product switch
    {
        0x001F or 0x0025 or 0x0032 or 0x003E or 0x003F or 0x0045 => new(0xFF, 1, true, false),
        0x0024 or 0x0044 => new(0xFF, 1, true, true),
        0x0059 => new(0x3F, 1, true, true), 0x005A => new(0x3F, 1, true, false),
        0x0062 or 0x0094 => new(0x1F, 400, false, false),
        0x0072 => new(0x3F, 31, true, false), 0x0073 => new(0x3F, 31, true, true),
        0x007A => new(0xFF, 60, true, true), 0x007B => new(0xFF, 60, true, false),
        0x007C => new(0x3F, 60, true, true), 0x007D => new(0x3F, 60, true, false),
        0x009E => new(0x1F, 60, true, true),
        0x009F or 0x00B3 or 0x00C1 => new(0x1F, 60, true, false),
        0x00B8 => new(0x1F, 60, false, false),
        0x009C or 0x00B4 or 0x00B9 or 0x00D4 => new(0x1F, 31, false, false),
        0x006F or 0x0077 or 0x0088 or 0x0090 or 0x009A or 0x00A6 or 0x00A8 or 0x00AB or 0x00B0 or
        0x00B7 or 0x00BF or 0x00C3 or 0x00C5 or 0x00C8 or 0x00CD or 0x00D1 or 0x00D7 => new(0x1F, 31, true, false),
        0x0070 or 0x0080 or 0x0086 or 0x008F or 0x00A5 or 0x00A7 or 0x00AA or 0x00AF or 0x00B6 or
        0x00BE or 0x00C0 or 0x00C2 or 0x00C4 or 0x00C7 or 0x00CC or 0x00D0 or 0x00D6 => new(0x1F, 31, true, true),
        0x00D3 => new(0x1F, 31, false, true),
        0x0258 or 0x0292 or 0x0298 or 0x02B9 or 0x02D7 => new(0x1F, 5, true, true, true),
        0x025A => new(0x3F, 5, true, true, true),
        0x025C or 0x0271 or 0x0290 or 0x0296 or 0x02BA or 0x02D5 => new(0x9F, 5, true, false, true),
        _ => null
    };

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle handle, [In] byte[] report, uint length);
}
