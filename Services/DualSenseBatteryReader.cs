using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class DualSenseBatteryReader
{
    // Protocol facts: Sony's upstream Linux hid-playstation input-report definitions.
    // https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c
    // Original read-only implementation. No output reports, calibration or mode changes.
    internal readonly record struct BatteryReading(string Level, bool Charging, bool? Low);

    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        if (device.VendorId != 0x054C || device.ProductId is not (0x0CE6 or 0x0DF2) ||
            caps.UsagePage != 1 || caps.Usage != 5) return null;
        var prefix = $"DualSense {device.InstanceId}; VID=054C PID={device.ProductId:X4}";
        string failure;
        if (caps.InputReportByteLength is not (64 or 78)) failure = "未声明可读取完整电量状态的手柄输入报告";
        else
        {
            try
            {
                // A separate shared HID handle receives its own reports; never request exclusive access.
                using var handle = HidNative.CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) failure = $"共享读取手柄失败，Windows 错误 {Marshal.GetLastWin32Error()}";
                else
                {
                    using var input = new FileStream(handle, FileAccess.Read, 1, isAsync: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    var report = new byte[caps.InputReportByteLength];
                    for (var read = 0; read < 96; read++)
                    {
                        var length = input.ReadAsync(report.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
                        if (length == 0) break;
                        if (!TryParseReport(report.AsSpan(0, length), caps.InputReportByteLength == 78, out var battery)) continue;
                        detail = $"{prefix}; 本次输入报告电量范围 {battery.Level}; charging={battery.Charging}";
                        return device with
                        {
                            Name = device.ProductId == 0x0DF2 ? "DualSense Edge 无线控制器" : "DualSense 无线控制器",
                            Manufacturer = "Sony", Category = "手柄", IsConnected = true, ConnectionKnown = true,
                            BatteryPercent = null, BatteryLevel = battery.Level, IsBatteryLow = battery.Low,
                            IsCharging = battery.Charging, UpdatedAt = DateTimeOffset.UtcNow,
                            Source = "DualSense HID 输入状态",
                            Message = "设备原生 10% 电量档位；显示范围，未换算成精确百分比"
                        };
                    }
                    failure = "本次未收到有效的完整电量报告；蓝牙简化输入不包含电量";
                }
            }
            catch (OperationCanceledException) { failure = "本次未收到有效电量报告（250 ms）"; }
            catch (IOException error) { failure = $"手柄状态读取失败：{error.Message}"; }
            catch (UnauthorizedAccessException error) { failure = $"手柄共享输入不可用：{error.Message}"; }
        }
        detail = $"{prefix}; {failure}";
        return device with { BatteryPercent = null, BatteryLevel = null, IsBatteryLow = null, IsCharging = null, UpdatedAt = null, Message = failure };
    }

    internal static bool TryParseReport(ReadOnlySpan<byte> report, bool bluetooth, out BatteryReading battery)
    {
        battery = default;
        if (bluetooth)
        {
            if (report.Length != 78 || report[0] != 0x31 ||
                InputCrc32(report[..74]) != BinaryPrimitives.ReadUInt32LittleEndian(report[74..])) return false;
        }
        else if (report.Length != 64 || report[0] != 1) return false;
        var status = report[bluetooth ? 54 : 53];
        var band = status & 15;
        var charging = status >> 4;
        // Error/unknown charging states must not become an artificial empty-battery reading.
        if (band > 10 || charging > 2) return false;
        var level = charging == 2 || band == 10 ? "满电" : $"{band * 10}–{band * 10 + 9}%";
        battery = new(level, charging == 1, charging == 2 ? false : band == 0 ? true : null);
        return true;
    }

    internal static uint InputCrc32(ReadOnlySpan<byte> report)
    {
        var crc = UpdateCrc32(0xFFFFFFFF, 0xA1);
        foreach (var value in report) crc = UpdateCrc32(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc32(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320 : crc >> 1;
        return crc;
    }
}
