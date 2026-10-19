using System.IO;
using System.Runtime.InteropServices;
using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class SteamControllerBatteryReader
{
    // Valve 2015 wireless-controller status packet: 01 00 04 0B, battery at payload byte 14.
    // https://github.com/torvalds/linux/blob/master/drivers/hid/hid-steam.c
    // Interface selection is documented by Valve's SDL Steam Controller driver (dongle MI 1..4).
    // Only observe status broadcasts. Never pair, disable lizard mode, change mappings or request calibration.
    internal static DeviceStatus? TryRead(string path, DeviceStatus receiver, HidNative.Caps caps, out string detail)
    {
        detail = "";
        if (receiver.VendorId != 0x28DE || receiver.ProductId != 0x1142 || caps.UsagePage < 0xFF00) return null;
        var iface = Enumerable.Range(1, 4).FirstOrDefault(value => path.Contains($"&mi_{value:X2}", StringComparison.OrdinalIgnoreCase));
        if (iface == 0) return null;
        var prefix = $"Steam Controller VID=28DE PID=1142 MI={iface:X2}";
        string failure;
        if (caps.InputReportByteLength is not (64 or 65)) failure = "厂商接口未声明可读取的 Steam 状态报告";
        else
        {
            try
            {
                using var handle = HidNative.CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) failure = $"共享读取 Steam 状态失败，Windows 错误 {Marshal.GetLastWin32Error()}";
                else
                {
                    using var input = new FileStream(handle, FileAccess.Read, 1, isAsync: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                    var report = new byte[caps.InputReportByteLength];
                    for (var read = 0; read < 192; read++)
                    {
                        var length = input.ReadAsync(report.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
                        if (length == 0) break;
                        if (!TryParseStatus(report.AsSpan(0, length), out var percent)) continue;
                        detail = $"{prefix}; 本次状态报告电量 {percent}%";
                        return receiver with
                        {
                            Id = $"steam:{receiver.ContainerId ?? receiver.Id}:{iface}",
                            Name = $"Steam Controller · {iface}", Manufacturer = "Valve", Category = "手柄",
                            Connection = "2.4G（Steam Controller）", ContainerId = null, InstanceId = null,
                            ReceiverContainerId = receiver.ContainerId, BluetoothAddress = null,
                            IsConnected = true, ConnectionKnown = true, HasProtocolConnectionState = true, BatteryPercent = percent,
                            BatteryLevel = null, IsBatteryLow = null, IsCharging = null,
                            Source = "Steam Controller HID 状态广播", UpdatedAt = DateTimeOffset.UtcNow,
                            Message = "设备原生电量百分比；状态报告未提供充电信息"
                        };
                    }
                    failure = "本次未收到有效电量广播；未使用历史或推算值";
                }
            }
            catch (OperationCanceledException) { failure = "本次未收到电量广播（600 ms）；设备可能未连接或尚未广播状态"; }
            catch (IOException error) { failure = $"Steam 状态读取失败：{error.Message}"; }
            catch (UnauthorizedAccessException error) { failure = $"Steam 共享输入不可用：{error.Message}"; }
        }
        detail = $"{prefix}; {failure}";
        return receiver with
        {
            BatteryPercent = null, BatteryLevel = null, IsBatteryLow = null, IsCharging = null,
            UpdatedAt = null, ConnectionKnown = false, IsConnected = false, HasProtocolConnectionState = true,
            Source = "Steam Controller HID 状态广播", Message = failure
        };
    }

    internal static bool TryParseStatus(ReadOnlySpan<byte> report, out int percent)
    {
        percent = 0;
        // Windows includes a leading zero for an unnumbered HID report. hidraw may omit it.
        if (report.Length == 65)
        {
            if (report[0] != 0) return false;
            report = report[1..];
        }
        if (report.Length != 64 || report[0] != 1 || report[1] != 0 || report[2] != 4 || report[3] != 11 || report[14] > 100) return false;
        percent = report[14];
        return true;
    }
}
