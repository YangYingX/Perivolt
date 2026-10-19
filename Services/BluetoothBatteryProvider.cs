using PowerHub.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace PowerHub.Services;

public sealed class BluetoothBatteryProvider
{
    private static readonly string[] Properties = ["System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.ContainerId"];
    private readonly Dictionary<string, Task<DeviceStatus>> pending = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<DeviceStatus>> ScanAsync(bool includeBle, List<string> diagnostics, CancellationToken cancellationToken)
    {
        foreach (var key in pending.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToArray()) pending.Remove(key);
        var devices = new List<DeviceStatus>();
        await EnumerateAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), false, devices, diagnostics, cancellationToken);
        await EnumerateAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true, devices, diagnostics, cancellationToken);
        if (!includeBle) return devices;

        for (var index = 0; index < devices.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var device = devices[index];
            // Never connect a sleeping/disconnected peripheral just to ask its battery.
            if (device.Connection != "蓝牙 BLE" || !device.IsConnected) continue;
            try
            {
                // A WinRT connection may outlive the UI timeout. Keep that operation
                // instead of starting another one on each automatic refresh.
                if (!pending.TryGetValue(device.Id, out var readingTask))
                {
                    readingTask = ReadBatteryAsync(device, CancellationToken.None);
                    pending[device.Id] = readingTask;
                }
                devices[index] = await readingTask.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken);
            }
            catch (TimeoutException)
            {
                devices[index] = device with { Message = "BLE 电量读取超时；保留其他接口的读取结果" };
                diagnostics.Add($"BLE：{device.Name} 读取超时");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                devices[index] = device with { Message = $"BLE 读取失败：{exception.Message}" };
                diagnostics.Add($"BLE：{device.Name}：{exception.Message}");
            }
            finally
            {
                if (pending.TryGetValue(device.Id, out var operation) && operation.IsCompleted) pending.Remove(device.Id);
            }
        }
        return devices;
    }

    private static async Task EnumerateAsync(string selector, bool lowEnergy, List<DeviceStatus> devices, List<string> diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(selector, Properties, DeviceInformationKind.AssociationEndpoint)
                .AsTask(cancellationToken).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            foreach (var device in found)
            {
                var connectionKnown = device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var state) && state is bool;
                var connected = state is true;
                if (!connectionKnown || !connected) continue;
                var address = device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var rawAddress)
                    ? rawAddress?.ToString()?.Replace(":", "").Replace("-", "").ToUpperInvariant() : null;
                var container = device.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var rawContainer)
                    && Guid.TryParse(rawContainer?.ToString(), out var guid) && guid != Guid.Empty ? guid.ToString("D") : null;
                devices.Add(new DeviceStatus
                {
                    Id = device.Id,
                    Name = device.Name,
                    Category = InferCategory(device.Name),
                    Connection = lowEnergy ? "蓝牙 BLE" : "蓝牙",
                    IsConnected = connected,
                    ConnectionKnown = connectionKnown,
                    BluetoothAddress = address,
                    ContainerId = container,
                    Source = "Windows 蓝牙",
                    Message = lowEnergy ? "已连接；等待标准 Battery Service 查询" : "经典蓝牙设备；尝试由系统 PnP 属性读取电量"
                });
            }
            diagnostics.Add($"{(lowEnergy ? "BLE" : "经典蓝牙")}：{found.Count} 个已配对设备中，{found.Count(device => device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var state) && state is true)} 个当前已连接");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add($"{(lowEnergy ? "BLE" : "经典蓝牙")}枚举失败：{exception.Message}");
        }
    }

    private static async Task<DeviceStatus> ReadBatteryAsync(DeviceStatus device, CancellationToken cancellationToken)
    {
        using var peripheral = await BluetoothLEDevice.FromIdAsync(device.Id).AsTask(cancellationToken);
        if (peripheral is null) return device with { Message = "Windows 未授权访问 BLE 设备，或设备已断开" };
        if (peripheral.ConnectionStatus != BluetoothConnectionStatus.Connected)
            return device with { IsConnected = false, Message = "设备已断开，跳过电量查询" };
        var serviceResult = await peripheral.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(cancellationToken);
        if (serviceResult.Status != GattCommunicationStatus.Success)
            return device with { Message = $"Battery Service 不可访问：{serviceResult.Status}" };

        int? percent = null;
        bool? charging = null;
        var errors = new List<string>();
        try
        {
            foreach (var service in serviceResult.Services)
            {
                var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Cached).AsTask(cancellationToken);
                if (characteristics.Status != GattCommunicationStatus.Success)
                {
                    errors.Add(characteristics.Status.ToString());
                    continue;
                }
                foreach (var characteristic in characteristics.Characteristics)
                {
                    var isLevel = characteristic.Uuid == GattCharacteristicUuids.BatteryLevel;
                    var isStatus = characteristic.Uuid == new Guid("00002bed-0000-1000-8000-00805f9b34fb");
                    if ((!isLevel && !isStatus) || !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) continue;
                    var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
                    if (result.Status != GattCommunicationStatus.Success) { errors.Add(result.Status.ToString()); continue; }
                    using var reader = Windows.Storage.Streams.DataReader.FromBuffer(result.Value);
                    var bytes = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(bytes);
                    if (isLevel && bytes.Length >= 1 && bytes[0] <= 100) percent ??= bytes[0];
                    // BAS 1.1: Flags (1 byte), Power State (little endian, 2 bytes).
                    // Battery Charge State is bits 5..6: 0 unknown, 1 charging,
                    // 2 discharging active, 3 discharging inactive.
                    if (isStatus && bytes.Length >= 3)
                    {
                        var state = (bytes[1] >> 5) & 0x03;
                        charging = state switch { 1 => true, 2 or 3 => false, _ => null };
                    }
                }
                // Keep charging and level from the same battery instance. Multi-cell
                // earbuds need a model adapter to label left/right/case reliably.
                if (percent.HasValue || charging.HasValue) break;
            }
        }
        finally { foreach (var service in serviceResult.Services) service.Dispose(); }

        return device with
        {
            BatteryPercent = percent,
            IsConnected = peripheral.ConnectionStatus == BluetoothConnectionStatus.Connected,
            IsCharging = charging,
            Source = percent.HasValue || charging.HasValue ? "BLE Battery Service" : "Windows 蓝牙",
            UpdatedAt = percent.HasValue || charging.HasValue ? DateTimeOffset.Now : null,
            Message = percent.HasValue ? "来自设备标准 Battery Service 的本次读取"
                : serviceResult.Services.Count == 0 ? "设备未暴露标准 Battery Service，需要型号协议适配"
                : $"未读取到标准电量{(errors.Count > 0 ? $"：{string.Join("、", errors.Distinct())}" : "，可能仅支持通知或厂商协议") }"
        };
    }

    private static string InferCategory(string name)
    {
        var value = name.ToLowerInvariant();
        if (value.Contains("keyboard") || value.Contains("键盘")) return "键盘";
        if (value.Contains("mouse") || value.Contains("鼠标")) return "鼠标";
        if (value.Contains("microphone") || value.Contains("麦克") || value.Contains("话筒")) return "麦克风";
        if (value.Contains("ear") || value.Contains("buds") || value.Contains("head") || value.Contains("耳") || value.Contains("星环")) return "耳机";
        if (value.Contains("controller") || value.Contains("gamepad") || value.Contains("手柄")) return "手柄";
        return "外设";
    }
}
