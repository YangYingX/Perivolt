using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PowerHub.Models;

namespace PowerHub.Services;

public sealed class WindowsPnpProvider
{
    // BatteryLife is documented. The second key is a compatibility fallback used by
    // Bluetooth drivers; it is not a documented Windows SDK contract.
    private static readonly PropertyKey BatteryLife = new(new("49cd1f76-5626-4b17-a4e8-18b4aa1a2213"), 10);
    private static readonly PropertyKey BluetoothBattery = new(new("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
    private static readonly PropertyKey ContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    public IReadOnlyList<DeviceStatus> Scan(List<string> diagnostics, IReadOnlyList<DeviceStatus> connectedBluetooth, CancellationToken cancellationToken)
    {
        var devices = new List<DeviceStatus>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x02 | 0x04);
        if (set == new IntPtr(-1))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法枚举 Windows 即插即用设备");

        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 259) diagnostics.Add($"PnP 枚举提前结束，Windows 错误 {error}");
                    break;
                }
                var idBuffer = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceIdW(set, ref info, idBuffer, idBuffer.Capacity, out _)) continue;
                var id = idBuffer.ToString();
                var connection = ReadConnection(info.DevInst);
                if (connection.Connection is null) continue;
                var containerBytes = ReadProperty(set, ref info, ContainerId, out _);
                var container = containerBytes is { Length: 16 } ? new Guid(containerBytes).ToString("D") : null;
                if (container == Guid.Empty.ToString("D")) container = null;
                var bluetooth = connection.Connection.StartsWith("蓝牙");
                if (bluetooth && !IsBluetoothConnected(connection.BluetoothAddress, container, connectedBluetooth)) continue;
                if (!bluetooth && !connection.IsConnected) continue;
                var battery = ReadPercent(set, ref info, BatteryLife);
                var source = "Windows 电量属性";
                if (battery is null)
                {
                    battery = ReadPercent(set, ref info, BluetoothBattery);
                    source = "Windows 蓝牙兼容属性";
                }
                var deviceClass = ReadRegistryString(set, ref info, 7);
                var peripheralClass = deviceClass is "HIDClass" or "Keyboard" or "Mouse" or "MEDIA";
                if (battery is null && !peripheralClass) continue;

                var name = ReadRegistryString(set, ref info, 12);
                if (string.IsNullOrWhiteSpace(name)) name = ReadRegistryString(set, ref info, 0);
                var manufacturer = ReadRegistryString(set, ref info, 11);
                var hardwareIds = ReadRegistryString(set, ref info, 1).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var match = Regex.Match(id, @"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                devices.Add(new DeviceStatus
                {
                    Id = id,
                    InstanceId = id,
                    ContainerId = container,
                    Name = string.IsNullOrWhiteSpace(name) ? "未知外设" : name,
                    Manufacturer = manufacturer,
                    Category = Regex.IsMatch(name, "microphone|麦克|话筒", RegexOptions.IgnoreCase) ? "麦克风"
                        : deviceClass switch { "Keyboard" => "键盘", "Mouse" => "鼠标", "MEDIA" => "音频设备", _ => "外设" },
                    Connection = connection.Connection,
                    IsConnected = true,
                    ConnectionKnown = true,
                    BluetoothAddress = connection.BluetoothAddress,
                    VendorId = match.Success ? Convert.ToInt32(match.Groups[1].Value, 16) : null,
                    ProductId = match.Success ? Convert.ToInt32(match.Groups[2].Value, 16) : null,
                    HardwareIds = hardwareIds,
                    BatteryPercent = battery,
                    UpdatedAt = battery.HasValue ? DateTimeOffset.Now : null,
                    Source = battery.HasValue ? source : "Windows PnP",
                    Message = battery.HasValue ? "系统缓存电量；设备实际报告时间由驱动决定" : "已识别设备；未发现系统电量属性，等待 HID / 蓝牙读取"
                });
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        diagnostics.Add($"Windows PnP：发现 {devices.Count} 个外设节点（合并前）");
        return devices;
    }

    internal static bool IsBluetoothConnected(string? address, string? container, IReadOnlyList<DeviceStatus> devices) =>
        devices.Any(device => device.ConnectionKnown && device.IsConnected &&
            (!string.IsNullOrEmpty(address) && string.Equals(address, device.BluetoothAddress, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(container) && string.Equals(container, device.ContainerId, StringComparison.OrdinalIgnoreCase)));

    internal static (string? Connection, bool IsConnected, string? BluetoothAddress) ReadConnection(uint devInst)
    {
        var connected = CM_Get_DevNode_Status(out var status, out _, devInst, 0) == 0
            && (status & 0x08) != 0 && (status & 0x400) == 0; // DN_STARTED, DN_HAS_PROBLEM
        for (var depth = 0; depth < 16; depth++)
        {
            var buffer = new StringBuilder(1024);
            if (CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Capacity, 0) != 0) break;
            var id = buffer.ToString();
            if (id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase))
            {
                var address = Regex.Match(id, @"(?:DEV_|BLUETOOTHDEVICE_)([0-9A-F]{12})|_([0-9A-F]{12})(?:\\|$)|&([0-9A-F]{12})_", RegexOptions.IgnoreCase);
                var value = address.Success ? address.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value.ToUpperInvariant() : null;
                // Bluetooth devnodes remain started after disconnect. Only AEP state confirms a live connection.
                return (id.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase) ? "蓝牙 BLE" : "蓝牙", false, value);
            }
            if (id.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) && !id.Split('\\')[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            {
                var key = new PropertyKey(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 17); // DEVPKEY_Device_Capabilities
                var bytes = new byte[4];
                uint length = 4;
                var removable = CM_Get_DevNode_PropertyW(devInst, ref key, out _, bytes, ref length, 0) == 0
                    && length == 4 && (BitConverter.ToUInt32(bytes) & 0x04) != 0; // CM_DEVCAP_REMOVABLE
                return removable ? ("USB / 接收器", connected, null) : default;
            }
            if (id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)) break;
            if (CM_Get_Parent(out var parent, devInst, 0) != 0) break;
            devInst = parent;
        }
        return default;
    }

    private static int? ReadPercent(IntPtr set, ref DeviceInfo info, PropertyKey key)
    {
        var bytes = ReadProperty(set, ref info, key, out var type);
        if (bytes is null) return null;
        uint value = type switch
        {
            0x03 when bytes.Length >= 1 => bytes[0],
            0x05 when bytes.Length >= 2 => BitConverter.ToUInt16(bytes),
            0x07 when bytes.Length >= 4 => BitConverter.ToUInt32(bytes),
            _ => uint.MaxValue
        };
        return value <= 100 ? (int)value : null;
    }

    private static byte[]? ReadProperty(IntPtr set, ref DeviceInfo info, PropertyKey key, out uint type)
    {
        var buffer = new byte[4096];
        if (SetupDiGetDevicePropertyW(set, ref info, ref key, out type, buffer, (uint)buffer.Length, out var required, 0))
            return buffer.AsSpan(0, (int)required).ToArray();
        return null;
    }

    private static string ReadRegistryString(IntPtr set, ref DeviceInfo info, uint property)
    {
        var buffer = new byte[8192];
        return SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, (uint)buffer.Length, out var required)
            ? Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length)).TrimEnd('\0') : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId) { public Guid FormatId = formatId; public uint PropertyId = propertyId; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceInfo info, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder id, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref PropertyKey key, out uint type, byte[] buffer, ref uint length, uint flags);
}
