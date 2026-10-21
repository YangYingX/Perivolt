using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using PowerHub.Models;
using static PowerHub.Native.HidNative;

namespace PowerHub.Services;

public sealed class HidBatteryProvider(PluginHost plugins)
{
    private readonly Dictionary<string, Task<ProbeResult>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _slots = new(4, 4);
    private sealed record ProbeResult(DeviceStatus Device, string Detail, IReadOnlyList<DeviceStatus>? Children = null);

    public IReadOnlyList<DeviceStatus> Scan(List<string> diagnostics, IReadOnlyList<DeviceStatus> connectedBluetooth, CancellationToken cancellationToken)
    {
        var devices = new List<DeviceStatus>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x02 | 0x10);
        if (set == new IntPtr(-1))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法枚举 HID 外设");

        lock (_pending)
            foreach (var key in _pending.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray()) _pending.Remove(key);
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new InterfaceInfo { Size = (uint)Marshal.SizeOf<InterfaceInfo>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 259) diagnostics.Add($"HID 枚举提前结束：Windows 错误 {error}");
                    break;
                }

                var nativeDevice = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref info, IntPtr.Zero, 0, out var required, ref nativeDevice);
                if (required < 6 || required > 65536) continue;
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref info, detail, required, out _, ref nativeDevice)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrEmpty(path)) continue;
                    var device = Describe(set, ref nativeDevice, path);
                    if (!device.ConnectionKnown || !device.IsConnected)
                    {
                        if (!device.Connection.StartsWith("蓝牙") || !WindowsPnpProvider.IsBluetoothConnected(device.BluetoothAddress, device.ContainerId, connectedBluetooth)) continue;
                        device = device with { IsConnected = true, ConnectionKnown = true };
                    }
                    Task<ProbeResult>? task;
                    var alreadyPending = false;
                    lock (_pending)
                    {
                        if (_pending.TryGetValue(path, out task)) alreadyPending = !task.IsCompleted;
                        else if (_slots.Wait(0))
                        {
                            task = Task.Run(() => { try { return Probe(path, device); } finally { _slots.Release(); } });
                            _pending[path] = task;
                        }
                    }

                    // HidD_GetFeature is synchronous and has no timeout. A worker owns its
                    // handle and preparsed data until it really returns, avoiding use-after-free.
                    // ponytail: at most four stalled drivers; process isolation if recovery is needed.
                    if (task is null || (alreadyPending && !task.IsCompleted) || !task.Wait(4200, cancellationToken))
                    {
                        var reason = task is null ? "HID 查询槽已占满，稍后重试" : "HID 查询超时；后台等待驱动返回，不重复发起查询";
                        devices.Add(device with { Message = reason });
                        diagnostics.Add($"HID {device.InstanceId}: {reason}");
                        continue;
                    }
                    var result = task.GetAwaiter().GetResult();
                    if (result.Children is { Count: > 0 }) devices.AddRange(result.Children);
                    else devices.Add(result.Device);
                    diagnostics.Add(result.Detail);
                    lock (_pending) _pending.Remove(path);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        diagnostics.Add($"HID：发现 {devices.Count} 个接口（合并前）；标准电量 {devices.Count(x => x.HasBattery)} 个");
        return devices;
    }

    private static DeviceStatus Describe(IntPtr set, ref DeviceInfo info, string path)
    {
        var id = new StringBuilder(1024);
        SetupDiGetDeviceInstanceIdW(set, ref info, id, (uint)id.Capacity, out _);
        var name = RegistryText(set, ref info, 12);
        if (string.IsNullOrWhiteSpace(name)) name = RegistryText(set, ref info, 0);
        var hardwareIds = RegistryText(set, ref info, 1).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var key = new PropertyKey(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
        var bytes = new byte[16];
        var container = SetupDiGetDevicePropertyW(set, ref info, ref key, out var type, bytes, 16, out var count, 0)
            && type == 0x0d && count == 16 ? new Guid(bytes) : Guid.Empty;
        var match = Regex.Match(path, @"vid_([0-9a-f]{4}).*pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
        var connection = WindowsPnpProvider.ReadConnection(info.DevInst);
        return new DeviceStatus
        {
            Id = path, InstanceId = id.Length > 0 ? id.ToString() : null,
            ContainerId = container == Guid.Empty ? null : container.ToString("D"),
            Name = string.IsNullOrWhiteSpace(name) ? "HID 外设" : name,
            Manufacturer = RegistryText(set, ref info, 11),
            HardwareIds = hardwareIds,
            VendorId = match.Success ? Convert.ToInt32(match.Groups[1].Value, 16) : null,
            ProductId = match.Success ? Convert.ToInt32(match.Groups[2].Value, 16) : null,
            Category = Classify(0, 0, name),
            Connection = connection.Connection ?? "HID（内置 / 虚拟）",
            IsConnected = connection.IsConnected,
            ConnectionKnown = connection.IsConnected,
            BluetoothAddress = connection.BluetoothAddress,
            Source = "HID 设备枚举",
            Message = "已发现 HID 接口；尚未读取电量"
        };
    }

    private ProbeResult Probe(string path, DeviceStatus device)
    {
        var detail = $"HID {device.InstanceId}";
        try
        {
            // Shared metadata handle; vendor readers open only their documented protocol collection.
            using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                return new(device with { Message = $"无法打开 HID 接口，Windows 错误 {error}；设备仍保留" }, $"{detail}: 打开失败 {error}");
            }
            var attributes = new Attributes { Size = (uint)Marshal.SizeOf<Attributes>() };
            if (HidD_GetAttributes(handle, ref attributes)) device = device with { VendorId = attributes.VendorId, ProductId = attributes.ProductId };
            var product = HidText(handle, HidD_GetProductString);
            var maker = HidText(handle, HidD_GetManufacturerString);
            var serial = HidText(handle, HidD_GetSerialNumberString);
            if (product.Length > 0) device = device with { Name = product };
            if (maker.Length > 0) device = device with { Manufacturer = maker };
            // Serial is intentionally confined to the locally exported diagnostics.
            detail += $"; VID={device.VendorId:X4}; PID={device.ProductId:X4}; Serial={serial}; Container={device.ContainerId}";
            if (!HidD_GetPreparsedData(handle, out var data))
                return new(device with { Message = "驱动未提供 HID 描述符，无法查询标准电量" }, $"{detail}; 描述符读取失败 {Marshal.GetLastWin32Error()}");
            try
            {
                if (HidP_GetCaps(data, out var caps) != Success)
                    return new(device with { Message = "HID 描述符解析失败" }, $"{detail}; HidP_GetCaps 失败");
                device = device with { Category = Classify(caps.UsagePage, caps.Usage, device.Name) };
                detail += $"; Usage={caps.UsagePage:X4}/{caps.Usage:X4}; InputBytes={caps.InputReportByteLength}; OutputBytes={caps.OutputReportByteLength}; FeatureBytes={caps.FeatureReportByteLength}";
                if (caps.UsagePage >= 0xFF00)
                    detail += $"; InputReports={DescribeReports(0, caps.NumberInputValueCaps, caps.NumberInputButtonCaps, data)}; OutputReports={DescribeReports(1, caps.NumberOutputValueCaps, caps.NumberOutputButtonCaps, data)}; FeatureReports={DescribeReports(Feature, caps.NumberFeatureValueCaps, caps.NumberFeatureButtonCaps, data)}";
                var vendor = plugins.ReadHid(new(path, device, caps.UsagePage, caps.Usage,
                    caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
                if (vendor is not null) return new(vendor.Device with { HasProtocolConnectionState = true },
                    $"{detail}; {vendor.Detail}", vendor.Children);
                var values = new ValueCaps[caps.NumberFeatureValueCaps];
                var valueCount = (ushort)values.Length;
                if (valueCount > 0 && HidP_GetValueCaps(Feature, values, ref valueCount, data) != Success) values = [];
                else values = values.Take(valueCount).ToArray();
                var buttons = new ButtonCaps[caps.NumberFeatureButtonCaps];
                var buttonCount = (ushort)buttons.Length;
                if (buttonCount > 0 && HidP_GetButtonCaps(Feature, buttons, ref buttonCount, data) != Success) buttons = [];
                else buttons = buttons.Take(buttonCount).ToArray();

                // USB-IF HUT 1.7: Generic Device Controls 0x06/0x20;
                // Battery System 0x85/0x64 (percent), 0x85/0x44 (charging).
                // https://www.usb.org/sites/default/files/hut1_7.pdf
                var batteryValues = values.Where(x => x.BitSize is > 0 and <= 32 && (x.IsRange != 0 || x.ReportCount == 1)
                    && ((x.UsagePage == 0x06 && x.Contains(0x20)) || (x.UsagePage == 0x85 && (x.Contains(0x64) || x.Contains(0x44))))).ToArray();
                var chargingButtons = buttons.Where(x => x.UsagePage == 0x85 && x.Contains(0x44)).ToArray();
                var reportIds = batteryValues.Select(x => x.ReportId).Concat(chargingButtons.Select(x => x.ReportId)).Distinct().ToArray();
                if (reportIds.Length == 0 || caps.FeatureReportByteLength == 0)
                    return new(device with { Message = "描述符未声明可查询的标准电池 Feature；厂商协议待适配（仅确认 HID 接口存在）" }, $"{detail}; 无标准电池 Feature");

                int? battery = null;
                bool? charging = null;
                var errors = new List<string>();
                foreach (var reportId in reportIds)
                {
                    var report = new byte[caps.FeatureReportByteLength];
                    report[0] = reportId;
                    if (!HidD_GetFeature(handle, report, (uint)report.Length))
                    {
                        errors.Add($"report {reportId:X2} Windows 错误 {Marshal.GetLastWin32Error()}");
                        continue;
                    }
                    foreach (var value in batteryValues.Where(x => x.ReportId == reportId))
                    {
                        if (value.UsagePage == 0x06 && TryValue(data, value, 0x20, report, out var strength) && value.LogicalMax > value.LogicalMin)
                            battery ??= (int)Math.Round((strength - value.LogicalMin) * 100d / ((long)value.LogicalMax - value.LogicalMin));
                        if (value.UsagePage == 0x85 && value.Contains(0x64) && value.UnitsExp == 0
                            && TryValue(data, value, 0x64, report, out var percent) && percent is >= 0 and <= 100) battery ??= (int)percent;
                        if (value.UsagePage == 0x85 && value.Contains(0x44) && TryValue(data, value, 0x44, report, out var flag) && flag is 0 or 1) charging ??= flag == 1;
                    }
                    foreach (var button in chargingButtons.Where(x => x.ReportId == reportId))
                    {
                        var length = HidP_MaxUsageListLength(Feature, 0x85, data);
                        if (length == 0 || length > 65536) continue;
                        var usages = new ushort[length];
                        if (HidP_GetUsages(Feature, 0x85, button.LinkCollection, usages, ref length, data, report, (uint)report.Length) == Success)
                            charging ??= usages.Take((int)length).Contains((ushort)0x44);
                    }
                }
                var message = battery.HasValue ? "已读取 HID 标准电量；接收器后端在线状态由设备协议决定"
                    : charging.HasValue ? "已读取标准充电状态；未获取有效电量百分比" : "标准电池字段查询失败、超出有效范围或返回未知值";
                if (errors.Count > 0) message += "；" + string.Join("；", errors);
                return new(device with
                {
                    BatteryPercent = battery, IsCharging = charging,
                    UpdatedAt = battery.HasValue || charging.HasValue ? DateTimeOffset.Now : null,
                    Source = battery.HasValue || charging.HasValue ? "HID 标准电池报告" : "HID 描述符",
                    Message = message
                }, $"{detail}; Reports={string.Join(",", reportIds.Select(x => x.ToString("X2")))}; {message}");
            }
            finally { HidD_FreePreparsedData(data); }
        }
        catch (Exception error)
        {
            return new(device with { Message = $"HID 查询失败：{error.Message}" }, $"{detail}; {error.GetType().Name}: {error.Message}");
        }
    }

    private static string DescribeReports(int type, ushort count, ushort buttonCount, IntPtr data)
    {
        if (count > 256 || buttonCount > 256) return "too many";
        var reports = new List<string>();
        if (count > 0)
        {
            var values = new ValueCaps[count];
            var status = HidP_GetValueCaps(type, values, ref count, data);
            if (status != Success) reports.Add($"error {status:X8}");
            else reports.AddRange(values.Take(count).Select(value => $"{value.ReportId:X2}:{value.BitSize}x{value.ReportCount}"));
        }
        if (buttonCount > 0)
        {
            var buttons = new ButtonCaps[buttonCount];
            var status = HidP_GetButtonCaps(type, buttons, ref buttonCount, data);
            if (status != Success) reports.Add($"error {status:X8}");
            else reports.AddRange(buttons.Take(buttonCount).Select(button => $"{button.ReportId:X2}:buttons"));
        }
        return reports.Count > 0 ? string.Join(",", reports.Distinct()) : "none";
    }

    private static bool TryValue(IntPtr data, ValueCaps value, ushort usage, byte[] report, out long result)
    {
        result = 0;
        if (!value.Contains(usage) || HidP_GetUsageValue(Feature, value.UsagePage, value.LinkCollection, usage, out var raw, data, report, (uint)report.Length) != Success) return false;
        result = value.LogicalMin < 0 ? (int)(raw << (32 - value.BitSize)) >> (32 - value.BitSize) : raw;
        return result >= value.LogicalMin && result <= value.LogicalMax;
    }

    private static string Classify(ushort page, ushort usage, string name)
    {
        // Composite keyboard receivers may also expose a mouse-emulation collection.
        if (Regex.IsMatch(name, "keyboard|键盘", RegexOptions.IgnoreCase)) return "键盘";
        if (Regex.IsMatch(name, "mouse|鼠标", RegexOptions.IgnoreCase)) return "鼠标";
        if (Regex.IsMatch(name, "microphone|麦克|话筒", RegexOptions.IgnoreCase)) return "麦克风";
        if (Regex.IsMatch(name, "headset|headphone|earbud|耳机", RegexOptions.IgnoreCase)) return "耳机";
        if (page == 1 && usage == 6) return "键盘";
        if (page == 1 && usage is 1 or 2) return "鼠标";
        if (page == 1 && usage is 4 or 5) return "手柄";
        if (page == 0x0b && usage == 5) return "耳机";
        return "外设";
    }

    private delegate bool HidStringReader(SafeFileHandle handle, byte[] buffer, uint length);
    private static string HidText(SafeFileHandle handle, HidStringReader reader)
    {
        var buffer = new byte[256];
        // Some virtual drivers return bytes beyond the first UTF-16 terminator.
        // They are not part of the string and must never enter diagnostics.
        return reader(handle, buffer, (uint)buffer.Length) ? Encoding.Unicode.GetString(buffer).Split('\0', 2)[0].Trim() : "";
    }

    private static string RegistryText(IntPtr set, ref DeviceInfo info, uint property)
    {
        var buffer = new byte[8192];
        return SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, (uint)buffer.Length, out var length)
            ? Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(length, (uint)buffer.Length)).TrimEnd('\0') : "";
    }
}
