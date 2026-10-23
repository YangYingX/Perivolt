using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PowerHub.Models;
using PowerHub.Native;
using static PowerHub.Native.HidNative;

namespace PowerHub.Services;

internal static class LogitechBatteryReader
{
    // HID++ packet/IRoot specification: https://github.com/Logitech/cpg-docs/tree/master/hidpp20
    // Receiver interface table adapted from BatteryMonitor, Copyright (c) 2026 JKWTCN (MIT):
    // https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/hid/LogitechHidProvider.cpp
    // Only feature discovery, capabilities, battery status and device-name reads are sent.
    private sealed record ReceiverProfile(int Interface, int Slots);
    private sealed record InputCollection(string Path, int Length, int OutputLength);
    internal readonly record struct BatteryReading(int? Percent, string? Level, bool? Charging, string Message);

    private static ReceiverProfile? Profile(int? product) => product switch
    {
        0xC548 or 0xC52B or 0xC532 => new(2, 6),
        0xC534 => new(1, 2),
        0xC52F or 0xC518 or 0xC51A or 0xC51B or 0xC521 or 0xC525 or
        0xC526 or 0xC52E or 0xC531 or 0xC535 or 0xC537 => new(1, 1),
        0xC539 or 0xC53A or 0xC53D or 0xC53F or 0xC541 or 0xC545 or
        0xC547 or 0xC54D => new(2, 1),
        _ => null
    };

    internal static IReadOnlyList<DeviceStatus>? TryRead(string path, DeviceStatus receiver, Caps caps, out string detail)
    {
        detail = "";
        var profile = Profile(receiver.ProductId);
        if (receiver.VendorId != 0x046D || profile is null || caps.UsagePage != 0xFF00 || caps.Usage != 1 ||
            !path.Contains($"&mi_{profile.Interface:X2}", StringComparison.OrdinalIgnoreCase)) return null;
        var devices = new List<DeviceStatus>();
        var prefix = $"Logitech HID++ VID=046D PID={receiver.ProductId:X4}";
        if (caps.OutputReportByteLength is < 7 or > 64 || caps.InputReportByteLength is < 7 or > 64 ||
            !Guid.TryParse(receiver.ContainerId, out var container))
        {
            detail = $"{prefix}; 缺少安全定位厂商读写集合所需的描述符或容器信息";
            return devices;
        }

        var batteryQueries = new List<string>();
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        try
        {
            var input = FindInputCollection(receiver.ProductId!.Value, profile.Interface, container, budget.Token);
            if (input is null) { detail = $"{prefix}; 同一接收器没有可读取的 HID++ 长报告集合"; return devices; }
            using var writerHandle = CreateFileW(path, 0x40000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            using var shortReaderHandle = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            using var readerHandle = CreateFileW(input.Path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            using var longWriterHandle = input.OutputLength >= 20
                ? CreateFileW(input.Path, 0x40000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero) : null;
            if (writerHandle.IsInvalid || shortReaderHandle.IsInvalid || readerHandle.IsInvalid)
            {
                detail = $"{prefix}; 厂商集合打开失败，Windows 错误 {Marshal.GetLastWin32Error()}";
                return devices;
            }
            using var writer = new FileStream(writerHandle, FileAccess.Write, 1, isAsync: true);
            using var shortReader = new FileStream(shortReaderHandle, FileAccess.Read, 1, isAsync: true);
            using var reader = new FileStream(readerHandle, FileAccess.Read, 1, isAsync: true);
            using var longWriter = longWriterHandle is { IsInvalid: false }
                ? new FileStream(longWriterHandle, FileAccess.Write, 1, isAsync: true) : null;
            byte softwareId = 12;
            var captureBatteryQueries = true;
            var longReportSlots = new HashSet<byte>();
            var slotUnavailable = false;
            byte[]? Query(byte slot, byte feature, byte function, byte p0 = 0, byte p1 = 0)
            {
                softwareId = (byte)(softwareId % 15 + 1);
                var command = (byte)((function << 4) | softwareId);
                var useLongReport = longReportSlots.Contains(slot);
                var response = Exchange(useLongReport ? longWriter! : writer, shortReader, reader,
                    useLongReport ? input.OutputLength : caps.OutputReportByteLength,
                    caps.InputReportByteLength, input.Length, slot, feature, command, p0, p1, useLongReport, budget.Token);
                // HID++ FAP can require a 0x11 request even when a 0x10 collection is exposed.
                // Retry only a silent read, on this receiver's descriptor-verified long collection.
                // A receiver error is an answer and must not trigger another transport or slot probe.
                if (response is null && !useLongReport && longWriter is not null && !budget.IsCancellationRequested)
                {
                    response = Exchange(longWriter, shortReader, reader, input.OutputLength,
                        caps.InputReportByteLength, input.Length, slot, feature, command, p0, p1, true, budget.Token);
                    if (response is not null) longReportSlots.Add(slot);
                }
                var error = response is not null && IsErrorResponse(response, slot, feature, command);
                if (response is not null && IsUnknownDeviceResponse(response, slot, feature, command)) slotUnavailable = true;
                if (captureBatteryQueries)
                    batteryQueries.Add($"slot={slot} feature={feature:X2} fn={function:X1} args={p0:X2}{p1:X2}: " +
                        (response is null ? "无匹配响应" : error ? $"设备错误 {response[5]:X2} ({Convert.ToHexString(response.AsSpan(0, 7))})" : Convert.ToHexString(response.AsSpan(0, 7))));
                return error ? null : response;
            }

            for (byte slot = 1; slot <= profile.Slots && !budget.IsCancellationRequested; slot++)
            {
                slotUnavailable = false;
                BatteryReading? battery = null;
                var unified = Query(slot, 0, 0, 0x10, 0x04);
                if (slotUnavailable)
                {
                    batteryQueries.Add($"slot={slot}: 接收器可达，但该槽位没有可访问的已连接设备（HID++ 1.0 UNKNOWN_DEVICE）");
                    continue;
                }
                if (unified is not null && unified[4] is > 0 and < 0xFF)
                {
                    var capabilities = Query(slot, unified[4], 0);
                    var status = capabilities is null ? null : Query(slot, unified[4], 1);
                    if (capabilities is not null && status is not null)
                    {
                        if (TryParseUnified(capabilities, status, out var reading)) battery = reading;
                        else batteryQueries.Add($"slot={slot}: 0x1004 响应字段未通过校验");
                    }
                }
                if (battery is null && !slotUnavailable && !budget.IsCancellationRequested)
                {
                    var legacy = Query(slot, 0, 0, 0x10, 0x00);
                    if (legacy is not null && legacy[4] is > 0 and < 0xFF)
                    {
                        var status = Query(slot, legacy[4], 0);
                        if (status is not null)
                        {
                            if (TryParseLevelStatus(status, out var reading)) battery = reading;
                            else batteryQueries.Add($"slot={slot}: 0x1000 响应字段未通过校验");
                        }
                    }
                }
                if (battery is not { } value) continue;
                devices.Add(receiver with
                {
                    Id = $"hidpp:{container:D}:{slot}",
                    Name = $"Logitech 外设 · {slot}",
                    Manufacturer = "Logitech", Category = "外设", Connection = "2.4G（HID++）",
                    ContainerId = null, InstanceId = null, ReceiverContainerId = receiver.ContainerId,
                    BluetoothAddress = null, ConnectionKnown = true, IsConnected = true,
                    BatteryPercent = value.Percent, BatteryLevel = value.Level, IsCharging = value.Charging,
                    Source = "Logitech HID++ 2.0", Message = value.Message, UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            captureBatteryQueries = false;
            // Names are optional; a slow name query must never discard a valid battery sample.
            for (var index = 0; index < devices.Count && !budget.IsCancellationRequested; index++)
            {
                var slot = byte.Parse(devices[index].Id[(devices[index].Id.LastIndexOf(':') + 1)..]);
                var feature = Query(slot, 0, 0, 0, 5);
                if (feature is null || feature[4] is 0 or 0xFF) continue;
                var count = Query(slot, feature[4], 0);
                if (count is null || count[4] is 0 or > 64) continue;
                var name = new List<byte>(count[4]);
                while (name.Count < count[4] && !budget.IsCancellationRequested)
                {
                    var fragment = Query(slot, feature[4], 1, (byte)name.Count);
                    if (fragment is null) break;
                    var take = Math.Min(count[4] - name.Count, fragment.Length - 4);
                    if (take <= 0) break;
                    name.AddRange(fragment.AsSpan(4, take).ToArray());
                }
                if (name.Count == count[4])
                {
                    var text = Encoding.UTF8.GetString(name.ToArray()).Trim('\0', ' ');
                    if (text.Length > 0 && !text.Any(char.IsControl)) devices[index] = devices[index] with { Name = text };
                }
            }
            detail = $"{prefix}; 本次收到 {devices.Count} 个在线子设备的有效电量，查询预算 700 ms";
        }
        catch (OperationCanceledException) { detail = $"{prefix}; 查询预算用尽；保留本次已验证的 {devices.Count} 个子设备"; }
        catch (IOException error) { detail = $"{prefix}; 电量接口不可用：{error.Message}"; }
        catch (UnauthorizedAccessException error) { detail = $"{prefix}; 无法读取厂商接口：{error.Message}"; }
        finally
        {
            if (batteryQueries.Count > 0) detail += "; " + string.Join("; ", batteryQueries);
        }
        return devices;
    }

    private static byte[]? Exchange(Stream writer, Stream shortReader, Stream longReader,
        int outputLength, int shortInputLength, int longInputLength,
        byte slot, byte feature, byte function, byte p0, byte p1, bool longReport, CancellationToken budget)
        => ExchangeAsync(writer, shortReader, longReader, outputLength, shortInputLength, longInputLength,
            slot, feature, function, p0, p1, longReport, budget).GetAwaiter().GetResult();

    internal static async Task<byte[]?> ExchangeAsync(Stream writer, Stream shortReader, Stream longReader,
        int outputLength, int shortInputLength, int longInputLength,
        byte slot, byte feature, byte function, byte p0, byte p1, bool longReport, CancellationToken budget)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget);
        timeout.CancelAfter(85);
        var request = new byte[outputLength];
        request[0] = longReport ? (byte)0x11 : (byte)0x10; request[1] = slot; request[2] = feature; request[3] = function;
        request[4] = p0; request[5] = p1;
        // Firmware may return short replies on usage 1 or long replies on usage 2.
        // Both are vendor collections; neither is the normal keyboard/mouse input stream.
        var shortRead = ReadMatchingAsync(shortReader, shortInputLength, slot, feature, function, timeout.Token);
        var longRead = ReadMatchingAsync(longReader, longInputLength, slot, feature, function, timeout.Token);
        try
        {
            await writer.WriteAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            var first = await Task.WhenAny(shortRead, longRead).ConfigureAwait(false);
            var result = await first.ConfigureAwait(false);
            return result ?? await (first == shortRead ? longRead : shortRead).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            timeout.Cancel();
            // Do not leave a pending HID read to consume the next transaction's reply.
            await Task.WhenAll(shortRead, longRead).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<byte[]?> ReadMatchingAsync(Stream reader, int inputLength,
        byte slot, byte feature, byte function, CancellationToken cancellation)
    {
        var response = new byte[inputLength];
        try
        {
            for (var read = 0; read < 32; read++)
            {
                var length = await reader.ReadAsync(response.AsMemory(), cancellation).ConfigureAwait(false);
                if (length == 0) return null;
                if (IsErrorResponse(response.AsSpan(0, length), slot, feature, function))
                    return response.AsSpan(0, response[0] == 0x10 ? 7 : 20).ToArray();
                if (ResponseMatches(response.AsSpan(0, length), slot, feature, function))
                    return response.AsSpan(0, response[0] == 0x10 ? 7 : 20).ToArray();
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }

    internal static bool ResponseMatches(ReadOnlySpan<byte> report, byte slot, byte feature, byte function) =>
        report.Length >= 7 && (report[0] == 0x10 || report[0] == 0x11 && report.Length >= 20) &&
        report[1] == slot && report[2] == feature && report[3] == function && (function & 15) != 0;

    internal static bool IsErrorResponse(ReadOnlySpan<byte> report, byte slot, byte feature, byte function) =>
        report.Length >= 7 && report[1] == slot &&
        (report[0] == 0x10 && report[2] == 0x8F || report[0] == 0x11 && report.Length >= 20 && report[2] == 0xFF) &&
        report[3] == feature && report[4] == function;

    internal static bool IsUnknownDeviceResponse(ReadOnlySpan<byte> report, byte slot, byte feature, byte function) =>
        IsErrorResponse(report, slot, feature, function) && report[0] == 0x10 && report[5] == 0x08;

    internal static bool TryParseUnified(ReadOnlySpan<byte> capabilities, ReadOnlySpan<byte> status, out BatteryReading reading)
    {
        reading = default;
        if (capabilities.Length < 7 || status.Length < 20 || status[0] != 0x11 ||
            capabilities[4] > 15 || status[4] > 100 ||
            status[5] is not (0 or 1 or 2 or 4 or 8) || status[6] > 4) return false;
        // Newer feature versions may set other capability flags; only bit 1 declares percentage support.
        var supportsPercent = (capabilities[5] & 2) != 0;
        var level = status[5] switch { 1 => "极低", 2 => "低", 4 => "中", 8 => "满", _ => null };
        if (!supportsPercent && (level is null || (capabilities[4] & status[5]) == 0)) return false;
        reading = new(supportsPercent ? status[4] : null, supportsPercent ? null : level,
            status[6] switch { 1 or 2 => true, 0 or 3 => false, _ => null },
            supportsPercent ? "设备上报的电量百分比（HID++ 0x1004）" : "设备仅上报电量档位（HID++ 0x1004）");
        return true;
    }

    internal static bool TryParseLevelStatus(ReadOnlySpan<byte> status, out BatteryReading reading)
    {
        reading = default;
        // Logitech's 0x1000 specification reserves zero for unknown, not an empty battery.
        if (status.Length < 7 || status[0] is not (0x10 or 0x11) || status[0] == 0x11 && status.Length < 20 ||
            status[4] is 0 or > 100 || status[5] > status[4] || status[6] > 7) return false;
        reading = new(status[4], null, status[6] switch { 1 or 2 or 4 => true, 0 or 3 => false, _ => null },
            "设备上报的电量等级值（HID++ 0x1000）");
        return true;
    }

    private static InputCollection? FindInputCollection(int product, int iface, Guid container, CancellationToken cancellation)
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x02 | 0x10);
        if (set == new IntPtr(-1)) return null;
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var info = new InterfaceInfo { Size = (uint)Marshal.SizeOf<InterfaceInfo>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref info)) break;
                var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref info, IntPtr.Zero, 0, out var required, ref device);
                if (required < 6 || required > 65536) continue;
                var memory = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(memory, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref info, memory, required, out _, ref device)) continue;
                    var path = Marshal.PtrToStringUni(memory + 4);
                    if (path is null || !path.Contains($"vid_046d&pid_{product:X4}", StringComparison.OrdinalIgnoreCase) ||
                        !path.Contains($"&mi_{iface:X2}", StringComparison.OrdinalIgnoreCase)) continue;
                    var key = new PropertyKey(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
                    var bytes = new byte[16];
                    if (!SetupDiGetDevicePropertyW(set, ref device, ref key, out var type, bytes, 16, out var count, 0) ||
                        type != 0x0D || count != 16 || new Guid(bytes) != container) continue;
                    using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var data)) continue;
                    try
                    {
                        if (HidP_GetCaps(data, out var caps) == Success && caps.UsagePage == 0xFF00 && caps.Usage == 2 &&
                            caps.InputReportByteLength is >= 20 and <= 64)
                            return new(path, caps.InputReportByteLength,
                                caps.OutputReportByteLength is >= 20 and <= 64 && HasLongOutputReport(data, caps)
                                    ? caps.OutputReportByteLength : 0);
                    }
                    finally { HidD_FreePreparsedData(data); }
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    private static bool HasLongOutputReport(IntPtr data, Caps caps)
    {
        if (caps.NumberOutputValueCaps is > 0 and <= 256)
        {
            var count = caps.NumberOutputValueCaps;
            var values = new ValueCaps[count];
            if (HidP_GetValueCaps(1, values, ref count, data) == Success && values.Take(count).Any(value => value.ReportId == 0x11)) return true;
        }
        if (caps.NumberOutputButtonCaps is > 0 and <= 256)
        {
            var count = caps.NumberOutputButtonCaps;
            var buttons = new ButtonCaps[count];
            if (HidP_GetButtonCaps(1, buttons, ref count, data) == Success && buttons.Take(count).Any(button => button.ReportId == 0x11)) return true;
        }
        return false;
    }
}
