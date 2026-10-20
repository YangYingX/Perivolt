using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PowerHub.Native;

internal static class HidNative
{
    internal const int Success = 0x00110000;
    internal const int Feature = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterfaceInfo { public uint Size; public Guid ClassGuid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Attributes { public uint Size; public ushort VendorId; public ushort ProductId; public ushort Version; }

    // Native HIDP_CAPS is 64 bytes on both x86 and x64 (hidpi.h).
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct Caps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(6)] public ushort OutputReportByteLength;
        [FieldOffset(8)] public ushort FeatureReportByteLength;
        [FieldOffset(46)] public ushort NumberInputButtonCaps;
        [FieldOffset(48)] public ushort NumberInputValueCaps;
        [FieldOffset(52)] public ushort NumberOutputButtonCaps;
        [FieldOffset(54)] public ushort NumberOutputValueCaps;
        [FieldOffset(58)] public ushort NumberFeatureButtonCaps;
        [FieldOffset(60)] public ushort NumberFeatureValueCaps;
    }

    // BOOLEAN fields are bytes, not Win32 BOOL. The union begins at byte 56.
    // https://learn.microsoft.com/windows-hardware/drivers/ddi/hidpi/ns-hidpi-_hidp_value_caps
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct ValueCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportId;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
        internal readonly bool Contains(ushort usage) => IsRange != 0 ? usage >= UsageMin && usage <= UsageMax : usage == UsageMin;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct ButtonCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportId;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
        internal readonly bool Contains(ushort usage) => IsRange != 0 ? usage >= UsageMin && usage <= UsageMax : usage == UsageMin;
    }

    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] byte[] buffer, uint length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetManufacturerString(SafeFileHandle handle, [Out] byte[] buffer, uint length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, [Out] byte[] buffer, uint length);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, uint length);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, [Out] ValueCaps[] caps, ref ushort count, IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetButtonCaps(int type, [Out] ButtonCaps[] caps, ref ushort count, IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr data, byte[] report, uint length);
    [DllImport("hid.dll")] internal static extern uint HidP_MaxUsageListLength(int type, ushort page, IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] usages, ref uint count, IntPtr data, byte[] report, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceInfo info, IntPtr detail, uint length, out uint required, ref DeviceInfo device);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo device, StringBuilder id, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo device, uint property, out uint type, [Out] byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceInfo device, ref PropertyKey key, out uint type, [Out] byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
