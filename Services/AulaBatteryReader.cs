using System.IO;
using System.Runtime.InteropServices;
using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class AulaBatteryReader
{
    // GET_DEVICE_INFO layout adapted from BatteryMonitor, Copyright (c) 2026 JKWTCN (MIT).
    // https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/AulaHidProvider.cpp
    // Only the six upstream 2.4G dongle PIDs and vendor-defined collections are queried.
    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        if (device.VendorId != 0x0C45 || device.ProductId is not (0xFEF8 or 0xFEF9 or 0xFEFA or 0xFEFC or 0xFEFD or 0xFEFE)
            || caps.UsagePage < 0xFF00 || device.ProductId == 0xFEFC && (caps.UsagePage != 0xFF80 || caps.Usage != 1)) return null;

        var prefix = $"AULA {device.InstanceId}; VID={device.VendorId:X4}; PID={device.ProductId:X4}";
        var failure = "未收到有效设备电量响应";
        // Both upstream report sizes include one zero report-ID byte on Windows.
        if (caps.OutputReportByteLength is not (33 or 65) || caps.InputReportByteLength is not (33 or 65))
            failure = "厂商接口未声明支持的 32 / 64 字节电量报告";
        else
        {
            try
            {
                using var handle = HidNative.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) failure = $"无法打开厂商电量接口，Windows 错误 {Marshal.GetLastWin32Error()}";
                else
                {
                    // Shared, overlapped access to this vendor collection never captures keyboard/mouse input.
                    using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                    using var totalTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(560));
                    var previousRequest = Array.Empty<byte>();
                    foreach (var protocol in new (byte Command, int Size, bool LastFlag)[] { (0x10, 56, true), (0x10, 48, true), (0xA0, 64, false) })
                    {
                        totalTimeout.Token.ThrowIfCancellationRequested();
                        var request = CreateRequest(caps.OutputReportByteLength, protocol.Command, protocol.Size, protocol.LastFlag);
                        // 32-byte reports have identical first requests for the two new-protocol lengths.
                        if (request.AsSpan().SequenceEqual(previousRequest)) continue;
                        previousRequest = request;
                        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalTimeout.Token);
                        attemptTimeout.CancelAfter(TimeSpan.FromMilliseconds(170));
                        try
                        {
                            stream.WriteAsync(request.AsMemory(), attemptTimeout.Token).AsTask().GetAwaiter().GetResult();
                            var response = new byte[caps.InputReportByteLength];
                            for (var packet = 0; packet < 4; packet++)
                            {
                                var length = stream.ReadAsync(response.AsMemory(), attemptTimeout.Token).AsTask().GetAwaiter().GetResult();
                                if (length == 0) break;
                                if (!TryParseResponse(response.AsSpan(0, length), request[2], request[3], out var percent, out var charging, out var workMode)) continue;
                                detail = $"{prefix}; command={request[2]:X2}; battery={percent?.ToString() ?? "unknown"}; charging={charging}; workMode={workMode}";
                                return device with
                                {
                                    Category = "键盘", BatteryPercent = percent, BatteryLevel = null, IsCharging = charging,
                                    ConnectionKnown = percent.HasValue, IsConnected = percent.HasValue,
                                    UpdatedAt = DateTimeOffset.Now, Source = "AULA HID 电量查询",
                                    Message = percent.HasValue ? "已读取厂商电量报告" : "设备报告有线工作状态；未提供有效电量百分比"
                                };
                            }
                        }
                        catch (OperationCanceledException) when (!totalTimeout.IsCancellationRequested)
                        {
                            failure = "电量查询未响应，继续尝试已知只读设备信息协议";
                        }
                    }
                }
            }
            catch (OperationCanceledException) { failure = "厂商电量查询超时；可能处于休眠或未连接无线外设"; }
            catch (IOException exception) { failure = $"厂商电量查询失败：{exception.Message}"; }
            catch (UnauthorizedAccessException exception) { failure = $"厂商电量接口不可用：{exception.Message}"; }
        }
        detail = $"{prefix}; {failure}";
        return device with
        {
            BatteryPercent = null, BatteryLevel = null, IsCharging = null, UpdatedAt = null, Message = failure,
            ConnectionKnown = false, IsConnected = false
        };
    }

    internal static byte[] CreateRequest(int reportLength, byte command, int contentSize, bool useLastFlag)
    {
        var chunkSize = reportLength - 1 - 8;
        var request = new byte[reportLength];
        request[1] = 0xAA;
        request[2] = command;
        request[3] = (byte)Math.Min(contentSize, chunkSize);
        request[7] = useLastFlag && contentSize <= chunkSize ? (byte)1 : (byte)0;
        // Address 0 reads the first page. Its 24/56 payload bytes include all battery fields.
        return request;
    }

    internal static bool TryParseResponse(ReadOnlySpan<byte> response, byte command, byte payloadSize,
        out int? percent, out bool charging, out byte workMode)
    {
        percent = null;
        charging = false;
        workMode = 0;
        if (response.Length < 28 || response[0] != 0 || response[1] != 0x55 || response[2] != command
            || response[3] != payloadSize || payloadSize < 19
            || response[4] != 0 || response[5] != 0) return false;
        var payload = response[9..];
        if (payload[17] > 100 || payload.Slice(4, 4).IndexOfAnyExcept((byte)0) < 0
            && payload[16] == 0 && payload[17] == 0 && payload[18] == 0) return false;
        workMode = payload[16];
        charging = payload[18] != 0;
        // Upstream treats workMode=0 as wired/unknown, rather than a real 0% reading.
        percent = workMode == 0 ? null : payload[17];
        return true;
    }
}
