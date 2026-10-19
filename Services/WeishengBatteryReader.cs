using System.IO;
using System.Runtime.InteropServices;
using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class WeishengBatteryReader
{
    // Battery query layout adapted from BatteryMonitor, Copyright (c) 2026 JKWTCN (MIT).
    // https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/VgnHidProvider.cpp
    // Only GetBatteryLevel is sent; no configuration, mode switch or firmware commands.
    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        if (device.VendorId != 0x320F || device.ProductId is not (0x5088 or 0x5055) ||
            caps.UsagePage != 0xFF1C || caps.Usage != 0x0092) return null;

        var prefix = $"Weisheng {device.InstanceId}; VID={device.VendorId:X4}; PID={device.ProductId:X4}";
        string failure;
        if (caps.OutputReportByteLength is < 8 or > 4096 || caps.InputReportByteLength is < 10 or > 4096)
            failure = "厂商接口未声明所需的电量查询报告";
        else
        {
            try
            {
                // Read only this vendor collection. Shared access never captures normal key/mouse input.
                using var handle = HidNative.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    failure = $"无法打开厂商电量接口，Windows 错误 {Marshal.GetLastWin32Error()}";
                }
                else
                {
                    using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                    var request = new byte[caps.OutputReportByteLength];
                    request[0] = 4;
                    request[3] = 26;
                    request[4] = 6;
                    stream.WriteAsync(request.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
                    var response = new byte[caps.InputReportByteLength];
                    failure = "未收到有效电量响应";
                    for (var attempt = 0; attempt < 8; attempt++)
                    {
                        var length = stream.ReadAsync(response.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
                        if (length == 0) break;
                        // Windows HID reads retain the numbered report ID. Never guess a payload offset.
                        if (length < 10 || response[0] != 4 || response[1] != 0 || response[2] != 0 ||
                            response[3] != 26 || response[4] != 6 || response[8] > 100 || response[9] > 1)
                        {
                            failure = $"电量响应校验失败（头 {Convert.ToHexString(response.AsSpan(0, Math.Min(length, 8)))}）";
                            continue;
                        }

                        var percent = response[8];
                        var charging = response[9] == 1;
                        detail = $"{prefix}; report=04; battery={percent}%; charging={charging}";
                        return device with
                        {
                            Category = "键盘",
                            BatteryPercent = percent,
                            BatteryLevel = null,
                            IsCharging = charging,
                            UpdatedAt = DateTimeOffset.Now,
                            Source = "Weisheng HID 电量查询",
                            Message = "已读取厂商电量报告"
                        };
                    }
                }
            }
            catch (OperationCanceledException) { failure = "厂商电量查询超时；可能处于休眠或未连接外设"; }
            catch (IOException exception) { failure = $"厂商电量查询失败：{exception.Message}"; }
            catch (UnauthorizedAccessException exception) { failure = $"厂商电量接口不可用：{exception.Message}"; }
        }

        detail = $"{prefix}; {failure}";
        return device with { BatteryPercent = null, BatteryLevel = null, IsCharging = null, UpdatedAt = null,
            ConnectionKnown = false, IsConnected = false, Source = "Weisheng HID 电量查询", Message = failure };
    }
}
