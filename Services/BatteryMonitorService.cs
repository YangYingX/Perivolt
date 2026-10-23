using PowerHub.Models;

namespace PowerHub.Services;

public sealed class BatteryMonitorService(PluginHost plugins)
{
    private readonly SemaphoreSlim scanLock = new(1, 1);
    private readonly WindowsPnpProvider pnp = new();
    private readonly HidBatteryProvider hid = new(plugins);
    private readonly BluetoothBatteryProvider bluetooth = new();
    private readonly XInputBatteryProvider xinput = new();

    public async Task<ScanResult> ScanAsync(bool includeBle, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await scanLock.WaitAsync(cancellationToken);
        try
        {
            progress?.Report("正在枚举 USB、蓝牙和接收器…");
            var diagnostics = new List<string>();
            // Keep WinRT calls on the caller's dispatcher when started by the UI.
            var bluetoothDevices = await bluetooth.ScanAsync(includeBle, diagnostics, cancellationToken);
            var pnpTask = Task.Run(() => ScanNative("Windows PnP", log => pnp.Scan(log, bluetoothDevices, cancellationToken)), cancellationToken);
            var hidTask = Task.Run(() => ScanNative("USB HID", log => hid.Scan(log, bluetoothDevices, cancellationToken)), cancellationToken);
            progress?.Report("正在合并设备与电量信息…");
            var pnpResult = await pnpTask;
            var hidResult = await hidTask;
            diagnostics.AddRange(pnpResult.Log);
            diagnostics.AddRange(hidResult.Log);
            var controllerDevices = xinput.Scan(diagnostics);
            var devices = MergeDevices(pnpResult.Devices.Concat(hidResult.Devices).Concat(bluetoothDevices).Concat(controllerDevices))
                .Where(device => device.ConnectionKnown && device.IsConnected).ToArray();
            diagnostics.Add($"当前已连接 {devices.Length} 台外设，{devices.Count(x => x.HasBattery)} 台读到电量；已过滤断开、内置及虚拟设备。");
            diagnostics.Add("USB：仅确认 USB 节点存在；有线外设与 2.4G 接收器的区分及无线连接状态需型号协议确认。");
            return new ScanResult(devices, diagnostics, DateTimeOffset.Now);
        }
        finally { scanLock.Release(); }
    }

    private static (IReadOnlyList<DeviceStatus> Devices, List<string> Log) ScanNative(string name, Func<List<string>, IReadOnlyList<DeviceStatus>> scan)
    {
        var log = new List<string>();
        try { return (scan(log), log); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { log.Add($"{name} 读取失败：{exception.Message}"); return ([], log); }
    }

    public static IReadOnlyList<DeviceStatus> MergeDevices(IEnumerable<DeviceStatus> readings)
    {
        var all = readings.ToList();
        var resolvedReceivers = all.Where(x => x.ReceiverContainerId is not null)
            .Select(x => x.ReceiverContainerId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = all.Where(x => x.ContainerId is null || !resolvedReceivers.Contains(x.ContainerId)).ToList();
        var parents = Enumerable.Range(0, items.Count).ToArray();
        int Root(int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }
        var identities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var keys = new[] { $"id:{item.Id}", item.ContainerId is { Length: > 0 } container ? $"container:{container}" : null,
                item.BluetoothAddress is { Length: > 0 } address ? $"bluetooth:{address}" : null,
                item.InstanceId is { Length: > 0 } instance ? $"instance:{instance}" : null };
            foreach (var key in keys.Where(x => x is not null))
            {
                if (identities.TryGetValue(key!, out var prior)) parents[Root(index)] = Root(prior);
                else identities[key!] = index;
            }
        }

        var merged = items.Select((item, index) => (item, root: Root(index))).GroupBy(x => x.root).Select(group =>
        {
            var members = group.Select(x => x.item).ToList();
            var battery = members.Where(x => x.HasBattery).OrderByDescending(BatteryPriority).ThenByDescending(x => x.UpdatedAt).FirstOrDefault();
            var named = members.OrderByDescending(NameQuality).First();
            var connection = members.OrderByDescending(x => x.HasProtocolConnectionState)
                .ThenByDescending(x => x.ConnectionKnown)
                .ThenByDescending(x => x.IsConnected)
                .ThenByDescending(x => x.Connection.StartsWith("蓝牙")).First();
            var charging = members.Where(x => x.IsCharging.HasValue).OrderByDescending(BatteryPriority).FirstOrDefault();
            var protocol = members.FirstOrDefault(x => x.HasProtocolConnectionState);
            var hardware = members.FirstOrDefault(x => x.VendorId.HasValue) ?? named;
            // BlackWidow V3 exposes mouse-emulation collections but is a wired keyboard.
            // PID reference: https://github.com/openrazer/openrazer/blob/master/driver/razerkbd_driver.h
            var wiredKeyboard = hardware.VendorId == 0x1532 && hardware.ProductId == 0x024E;
            var stableId = members.Select(x => x.BluetoothAddress).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) is { } address
                ? $"bluetooth:{address}" : members.Select(x => x.ContainerId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) is { } container
                ? $"container:{container}" : members[0].Id;
            return named with
            {
                Id = stableId,
                Category = wiredKeyboard ? "键盘" : named.Category != "外设" ? named.Category : members.FirstOrDefault(x => x.Category != "外设")?.Category ?? "外设",
                Manufacturer = members.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Manufacturer))?.Manufacturer ?? "",
                Connection = wiredKeyboard ? "USB 有线" : connection.Connection,
                IsConnected = connection.IsConnected,
                ConnectionKnown = connection.ConnectionKnown,
                HasProtocolConnectionState = connection.HasProtocolConnectionState,
                BatteryPercent = battery?.BatteryPercent,
                BatteryLevel = battery?.BatteryLevel,
                IsBatteryLow = battery?.IsBatteryLow,
                PowerSource = members.Select(x => x.PowerSource).FirstOrDefault(x => x is not null),
                IsCharging = charging?.IsCharging,
                Source = battery?.Source ?? charging?.Source ?? protocol?.Source ?? named.Source,
                Message = battery?.Message ?? charging?.Message ?? protocol?.Message ?? members.OrderByDescending(x => x.Source.StartsWith("HID")).First().Message,
                UpdatedAt = battery?.UpdatedAt ?? charging?.UpdatedAt,
                ContainerId = members.Select(x => x.ContainerId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                InstanceId = members.Select(x => x.InstanceId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                BluetoothAddress = members.Select(x => x.BluetoothAddress).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                VendorId = hardware.VendorId,
                ProductId = hardware.ProductId,
                HardwareIds = members.SelectMany(x => x.HardwareIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        });
        return merged.OrderByDescending(x => x.IsConnected).ThenByDescending(x => x.HasBattery).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static int BatteryPriority(DeviceStatus device) => device.Source.StartsWith("BLE") ? 100 : device.HasProtocolConnectionState ? 95 : device.Source.Contains("HID") ? 90 : 50;
    private static int NameQuality(DeviceStatus device)
    {
        var name = device.Name.ToLowerInvariant();
        return (device.Connection.StartsWith("蓝牙") ? 10 : 0) + (device.Source == "Windows 蓝牙" ? 5 : 0) +
            (name.Contains("hid") || name.Contains("usb input") || name.Contains("usb 输入") || name.Contains("usb device")
                || name.Contains("符合") || name.Contains("未知") || name.Contains("音频端点") ? 0 : 20);
    }
}
