using System.IO;
using System.Runtime.InteropServices;
using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class HeadsetFamilyBatteryReader
{
    // Independently implemented from public protocol facts, not linked to vendor utilities.
    // Sources and exact family boundaries: docs/protocol-families.md.
    internal enum Protocol { NovaDiscrete, NovaPercent, CorsairVoid, HyperxCloud }
    internal sealed record Reading(bool Online, int? Percent = null, string? Level = null, bool? Charging = null, bool? Low = null);

    internal static Protocol? Match(string path, DeviceStatus device, HidNative.Caps caps)
    {
        if (caps.InputReportByteLength is < 5 or > 129 || caps.OutputReportByteLength is < 2 or > 65) return null;
        if (device.VendorId == 0x1038 && caps.UsagePage == 0xFFC0 && caps.Usage == 1
            && path.Contains("&mi_03", StringComparison.OrdinalIgnoreCase))
            return device.ProductId switch
            {
                0x2202 or 0x2206 or 0x220A or 0x223A or 0x227A or 0x22A4 => Protocol.NovaDiscrete,
                0x22A1 or 0x227E or 0x2258 or 0x229E or 0x22AD or 0x22A9 or 0x22A5 or 0x22A7 or 0x2298 => Protocol.NovaPercent,
                _ => null
            };
        if (device.VendorId == 0x1B1C && caps.UsagePage == 0xFFC5 && caps.Usage == 1
            && path.Contains("&mi_03", StringComparison.OrdinalIgnoreCase)
            && device.ProductId is 0x1B1C or 0x1B27 or 0x0A14 or 0x0A16 or 0x0A17 or 0x0A1D or 0x0A1A
                or 0x1B2A or 0x1B23 or 0x1B29 or 0x0A55 or 0x0A51 or 0x0A52 or 0x0A38 or 0x0A4F or 0x0A2B or 0x0A75 or 0x0A56)
            return Protocol.CorsairVoid;
        if (device.VendorId == 0x03F0 && device.ProductId is 0x05B7 or 0x0C9D or 0x069F or 0x0995
            && caps.UsagePage == 0xFF13 && caps.Usage == 1 && caps.InputReportByteLength is >= 62 and <= 65
            && caps.OutputReportByteLength is >= 62 and <= 65) return Protocol.HyperxCloud;
        return null;
    }

    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        if (Match(path, device, caps) is not { } protocol) return null;
        var source = protocol switch
        {
            Protocol.CorsairVoid => "Corsair VOID 系列电量协议",
            Protocol.HyperxCloud => "HyperX Cloud 系列电量协议",
            _ => "SteelSeries Nova 7 系列电量协议"
        };
        var failure = "未收到有效电量报告，未确认耳机在线";
        Reading? reading = null;
        try
        {
            using var handle = HidNative.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) failure = $"无法打开厂商接口，Windows 错误 {Marshal.GetLastWin32Error()}";
            else
            {
                var outputId = protocol switch { Protocol.CorsairVoid => (byte)0xC9, Protocol.HyperxCloud => (byte)0x66, _ => (byte)0 };
                var inputId = protocol switch { Protocol.CorsairVoid => (byte)0x64, Protocol.HyperxCloud => (byte)0x66, _ => (byte)0xB0 };
                var declaredInput = HidBatteryQuery.DeclaresReport(handle, 0, inputId)
                    || protocol is Protocol.NovaDiscrete or Protocol.NovaPercent && HidBatteryQuery.DeclaresReport(handle, 0, 0);
                if (!declaredInput || !HidBatteryQuery.DeclaresReport(handle, 1, outputId))
                    failure = "描述符未声明对应状态报告，未发送查询";
                else
                {
                    using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                    if (protocol == Protocol.HyperxCloud)
                    {
                        // Confirm the radio link first; cached percentage alone is insufficient.
                        var online = Exchange(stream, CreateRequest(protocol, caps.OutputReportByteLength, 0x82), caps.InputReportByteLength,
                            report => ParseHyperxOnline(report), timeout.Token);
                        if (online is { Online: false }) reading = online;
                        else if (online is { Online: true })
                        {
                            reading = Exchange(stream, CreateRequest(protocol, caps.OutputReportByteLength, 0x89), caps.InputReportByteLength,
                                ParseHyperxBattery, timeout.Token);
                            if (reading is { Online: true })
                            {
                                try
                                {
                                    var charge = Exchange(stream, CreateRequest(protocol, caps.OutputReportByteLength, 0x8A), caps.InputReportByteLength,
                                        ParseHyperxCharging, timeout.Token);
                                    if (charge is not null) reading = reading with { Charging = charge.Charging };
                                }
                                catch (OperationCanceledException) { /* Keep confirmed battery; charging stays unknown. */ }
                            }
                        }
                    }
                    else
                        reading = Exchange(stream, CreateRequest(protocol, caps.OutputReportByteLength), caps.InputReportByteLength,
                            report => Parse(report, protocol), timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) { failure = "电量查询超过600毫秒；未确认有效电量"; }
        catch (IOException error) { failure = $"电量接口读取失败：{error.Message}"; }
        catch (UnauthorizedAccessException error) { failure = $"厂商接口不可用：{error.Message}"; }
        var onlineNow = reading?.Online == true;
        var message = reading is null ? failure : onlineNow ? "已读取耳机系列状态与电量" : "接收器确认耳机离线，已清除电量";
        detail = $"{source}; VID={device.VendorId:X4}; PID={device.ProductId:X4}; {message}";
        return device with
        {
            Category = "耳机", Connection = protocol == Protocol.CorsairVoid ? "USB / 2.4G" : "2.4G",
            ConnectionKnown = reading is not null, IsConnected = onlineNow,
            BatteryPercent = onlineNow ? reading!.Percent : null, BatteryLevel = onlineNow ? reading!.Level : null,
            IsCharging = onlineNow ? reading!.Charging : null, IsBatteryLow = onlineNow ? reading!.Low : null,
            UpdatedAt = onlineNow ? DateTimeOffset.Now : null, Source = source, Message = message
        };
    }

    private delegate Reading? ParseReport(ReadOnlySpan<byte> report);

    private static Reading? Exchange(FileStream stream, byte[] request, int inputLength, ParseReport parse, CancellationToken token)
    {
        stream.WriteAsync(request.AsMemory(), token).AsTask().GetAwaiter().GetResult();
        var buffer = new byte[inputLength];
        for (var packet = 0; packet < 12; packet++)
        {
            var count = stream.ReadAsync(buffer.AsMemory(), token).AsTask().GetAwaiter().GetResult();
            if (count == 0) break;
            var report = buffer.AsSpan(0, count);
            if (report.SequenceEqual(request)) continue;
            if (parse(report) is { } result) return result;
        }
        return null;
    }

    internal static byte[] CreateRequest(Protocol protocol, int length, byte command = 0)
    {
        var request = new byte[length];
        request[0] = protocol switch { Protocol.CorsairVoid => (byte)0xC9, Protocol.HyperxCloud => (byte)0x66, _ => (byte)0 };
        request[1] = protocol switch { Protocol.CorsairVoid => (byte)0x64, Protocol.HyperxCloud => command, _ => (byte)0xB0 };
        return request;
    }

    internal static Reading? Parse(ReadOnlySpan<byte> report, Protocol protocol)
    {
        if (protocol == Protocol.CorsairVoid)
        {
            if (report.Length < 5 || report[0] != 0x64 || report[1] != 0 || report[3] != 0xB1) return null;
            if (report[4] == 0) return new(false);
            var percent = report[2] & 0x7F;
            return percent <= 100 && report[4] is 1 or 2 or 4 or 5
                ? new(true, percent, Charging: report[4] is 4 or 5, Low: report[4] == 2) : null;
        }
        if (protocol is not (Protocol.NovaDiscrete or Protocol.NovaPercent)) return null;
        // Numbered B0 reports include B0; unnumbered Windows reports carry an extra zero byte.
        if (report.Length > 0 && report[0] == 0) report = report[1..];
        if (report.Length < 4 || report[0] != 0xB0 || report[1] > 3) return null;
        if (report[1] != 3) return new(false);
        if (report[3] > 2) return null;
        var charging = report[3] == 1;
        if (protocol == Protocol.NovaDiscrete)
            return report[2] <= 4 ? new(true, Level: $"档位 {report[2]}/4", Charging: charging, Low: report[2] <= 1) : null;
        return report[2] <= 100 ? new(true, report[2], Charging: charging) : null;
    }

    internal static Reading? ParseHyperxOnline(ReadOnlySpan<byte> report) =>
        report.Length >= 5 && report[0] == 0x66 && report[1] is 0x82 or 0x0B && report[2] <= 1
            ? new(report[2] == 1) : null;

    internal static Reading? ParseHyperxBattery(ReadOnlySpan<byte> report) =>
        report.Length >= 5 && report[0] == 0x66 && report[1] is 0x89 or 0x0D
            && (report[2] != 0 || report[3] != 0) && report[4] <= 100 ? new(true, report[4]) : null;

    internal static Reading? ParseHyperxCharging(ReadOnlySpan<byte> report) =>
        report.Length >= 5 && report[0] == 0x66 && report[1] is 0x8A or 0x0C && report[2] <= 2
            ? new(true, Charging: report[2] == 1) : null;
}
