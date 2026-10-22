using System.Runtime.InteropServices;

namespace Perivolt.Extensions.PeripheralHub;

internal static class AudioInterop
{
    internal const uint DeviceStateActive = 1;
    internal const uint ClassContextAll = 23;
    internal static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    internal static readonly Guid PolicyClass = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    internal static readonly Guid EndpointVolumeInterface = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    internal static readonly PropertyKey FriendlyName = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    internal enum DataFlow { Render, Capture, All }
    internal enum Role { Console, Multimedia, Communications }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid format, uint id)
    {
        internal Guid Format = format;
        internal uint Id = id;
    }

    // PROPVARIANT's union starts at offset 8; Windows x64 requires a 24-byte value.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal IntPtr String;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow flow, uint stateMask, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow flow, Role role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    // IID_IMMDeviceCollection from the Windows SDK mmdeviceapi.h.
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float decibels, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float value, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float value);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float value, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float value);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }

    // Windows has no supported public setter for the system default endpoint.
    // This undocumented COM layout is isolated here; only SetDefaultEndpoint is called.
    // Callers must handle its absence/failure and offer Windows Sound Settings.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int effectsStore, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int effectsStore, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
