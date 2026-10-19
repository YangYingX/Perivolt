using System.IO;
using System.Runtime.InteropServices;
using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class CompxBatteryReader
{
    // Battery/online frame reference: Copyright (c) 2026 Andrew Rabert (MIT),
    // https://github.com/andrewrabert/python-pulsar-mouse-tool/blob/main/pulsar.py
    // and BatteryMonitor, Copyright (c) 2026 JKWTCN (MIT), VgnHidProvider.cpp / MouseEnc.
    // Exact additional transport IDs are documented in HaloBattery/docs/protocols.md.
    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        var wired = IsWired(device.VendorId, device.ProductId);
        if (wired is null || caps.UsagePage != 0xFF02 || caps.Usage != 2) return null;
        var prefix = $"Compx VID={device.VendorId:X4}; PID={device.ProductId:X4}";
        var failure = "厂商接口未声明所需的17字节电量报告";
        var confirmedOffline = false;
        if (caps.InputReportByteLength is >= 17 and <= 4096 && caps.OutputReportByteLength is >= 17 and <= 4096)
        {
            try
            {
                // Shared overlapped I/O is confined to the vendor collection, never normal mouse input.
                using var handle = HidNative.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) failure = $"无法打开厂商电量接口，Windows 错误 {Marshal.GetLastWin32Error()}";
                else
                {
                    using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                    var online = Exchange(stream, 3, caps, timeout.Token);
                    if (online is null) failure = "未收到有效在线响应；无线外设可能休眠或未连接";
                    else if (online[6] == 0)
                    {
                        confirmedOffline = true;
                        failure = "接收器确认无线外设未连接或休眠";
                    }
                    else
                    {
                        var response = Exchange(stream, 4, caps, timeout.Token);
                        if (response is null) failure = "未收到通过校验的电量响应";
                        else
                        {
                            var percent = response[6];
                            var charging = response[7] == 1;
                            detail = $"{prefix}; report=08; battery={percent}%; charging={charging}";
                            return device with
                            {
                                Category = "鼠标", Connection = wired.Value ? "USB 有线" : "2.4G",
                                ConnectionKnown = true, IsConnected = true, BatteryPercent = percent, BatteryLevel = null,
                                IsCharging = charging, UpdatedAt = DateTimeOffset.Now, Source = "Compx HID 电量查询",
                                Message = "已确认外设在线并读取厂商电量与充电报告"
                            };
                        }
                    }
                }
            }
            catch (OperationCanceledException) { failure = "厂商电量查询超过600毫秒；未确认有效电量"; }
            catch (IOException exception) { failure = $"厂商电量查询失败：{exception.Message}"; }
            catch (UnauthorizedAccessException exception) { failure = $"厂商电量接口不可用：{exception.Message}"; }
        }
        detail = $"{prefix}; {failure}";
        return device with
        {
            Category = "鼠标", BatteryPercent = null, BatteryLevel = null, IsCharging = null, UpdatedAt = null,
            Source = "Compx HID 电量查询", Message = failure,
            ConnectionKnown = confirmedOffline || wired.Value && device.ConnectionKnown,
            IsConnected = !confirmedOffline && wired.Value && device.IsConnected
        };
    }

    private static byte[]? Exchange(FileStream stream, byte command, HidNative.Caps caps, CancellationToken token)
    {
        var request = CreateRequest(command, caps.OutputReportByteLength);
        stream.WriteAsync(request.AsMemory(), token).AsTask().GetAwaiter().GetResult();
        var response = new byte[caps.InputReportByteLength];
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var length = stream.ReadAsync(response.AsMemory(), token).AsTask().GetAwaiter().GetResult();
            if (length == 0) return null;
            if (TryParseResponse(response.AsSpan(0, length), command)) return response;
        }
        return null;
    }

    internal static byte[] CreateRequest(byte command, int length = 17)
    {
        var request = new byte[length];
        request[0] = 8;
        request[1] = command;
        request[16] = unchecked((byte)(0x55 - 8 - command));
        return request;
    }

    internal static bool TryParseResponse(ReadOnlySpan<byte> report, byte command)
    {
        if (report.Length < 17 || report[0] != 8 || report[1] != command || report[2] != 0 ||
            report[..17].SequenceEqual(CreateRequest(command))) return false;
        var checksum = 0;
        for (var index = 0; index < 17; index++) checksum += report[index];
        if ((checksum & 0xFF) != 0x55) return false;
        return command == 3 || command == 4 && report[6] <= 100 && report[7] <= 1;
    }

    private static bool? IsWired(int? vendor, int? product) => (vendor, product) switch
    {
        (0x3554, 0xF507 or 0xF58C or 0xF58F) => true,
        (0x3554, 0xF508 or 0xF509 or 0xF58A) => false,
        (0x373B, 0x1085) => false,
        (0x3710, 0x3414) => true,
        (0x3710, 0x5406) => false,
        _ => null
    };
}
