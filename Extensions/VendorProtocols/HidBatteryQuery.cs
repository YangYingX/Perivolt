using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

// Shared by the headset and keyboard families that expose byte-array vendor reports.
// The caller must match a documented hardware family before opening this transport.
internal static class HidBatteryQuery
{
    internal delegate DeviceStatus? ParseResponse(ReadOnlySpan<byte> payload);

    internal static DeviceStatus? Read(string path, HidNative.Caps caps, byte[] payload,
        byte? requiredReportId, ParseResponse parse, out string detail)
    {
        detail = "未收到有效电量响应；外设可能未连接或处于休眠";
        try
        {
            using var handle = HidNative.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                detail = $"无法打开厂商接口，Windows 错误 {Marshal.GetLastWin32Error()}";
                return null;
            }
            if (!TryGetReportIds(handle, out var inputId, out var outputId)
                || requiredReportId.HasValue && (inputId != requiredReportId || outputId != requiredReportId)
                || payload.Length != caps.OutputReportByteLength - 1)
            {
                detail = "HID 描述符与电量协议不匹配，未发送查询";
                return null;
            }
            var request = new byte[caps.OutputReportByteLength];
            request[0] = outputId;
            payload.CopyTo(request, 1);
            using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            stream.WriteAsync(request.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
            var response = new byte[caps.InputReportByteLength];
            for (var packet = 0; packet < 12; packet++)
            {
                var length = stream.ReadAsync(response.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
                if (length == 0) break;
                var report = response.AsSpan(0, length);
                if (length != caps.InputReportByteLength || report[0] != inputId || report.SequenceEqual(request)) continue;
                var reading = parse(report[1..]);
                if (reading is null) continue;
                detail = $"input={inputId:X2}; output={outputId:X2}; {reading.Message}";
                return reading;
            }
        }
        catch (OperationCanceledException) { detail = "电量查询超过600毫秒；未确认外设在线"; }
        catch (IOException error) { detail = $"厂商电量查询失败：{error.Message}"; }
        catch (UnauthorizedAccessException error) { detail = $"厂商接口不可用：{error.Message}"; }
        return null;
    }

    private static bool TryGetReportIds(SafeFileHandle handle, out byte input, out byte output)
    {
        input = output = 0;
        if (!HidNative.HidD_GetPreparsedData(handle, out var data)) return false;
        try
        {
            if (HidNative.HidP_GetCaps(data, out var caps) != HidNative.Success) return false;
            return TryGetId(0, caps.NumberInputValueCaps, caps.InputReportByteLength, data, out input)
                && TryGetId(1, caps.NumberOutputValueCaps, caps.OutputReportByteLength, data, out output);
        }
        finally { HidNative.HidD_FreePreparsedData(data); }
    }

    private static bool TryGetId(int type, ushort count, ushort reportLength, IntPtr data, out byte id)
    {
        id = 0;
        if (count is 0 or > 256) return false;
        var values = new HidNative.ValueCaps[count];
        if (HidNative.HidP_GetValueCaps(type, values, ref count, data) != HidNative.Success) return false;
        return TrySelectReportId(values.AsSpan(0, count), reportLength, out id);
    }

    internal static bool TrySelectReportId(ReadOnlySpan<HidNative.ValueCaps> values, int reportLength, out byte id)
    {
        id = 0;
        var found = false;
        foreach (var value in values)
        {
            if (value.BitSize != 8 || value.ReportCount != reportLength - 1) continue;
            if (found && id != value.ReportId) return false;
            id = value.ReportId;
            found = true;
        }
        return found;
    }

    internal static bool DeclaresReport(SafeFileHandle handle, int type, byte id)
    {
        if (!HidNative.HidD_GetPreparsedData(handle, out var data)) return false;
        try
        {
            if (HidNative.HidP_GetCaps(data, out var caps) != HidNative.Success) return false;
            var count = type == 0 ? caps.NumberInputValueCaps : caps.NumberOutputValueCaps;
            if (count is > 0 and <= 256)
            {
                var values = new HidNative.ValueCaps[count];
                if (HidNative.HidP_GetValueCaps(type, values, ref count, data) == HidNative.Success
                    && values.Take(count).Any(value => value.ReportId == id)) return true;
            }
            count = type == 0 ? caps.NumberInputButtonCaps : caps.NumberOutputButtonCaps;
            if (count is > 0 and <= 256)
            {
                var buttons = new HidNative.ButtonCaps[count];
                return HidNative.HidP_GetButtonCaps(type, buttons, ref count, data) == HidNative.Success
                    && buttons.Take(count).Any(button => button.ReportId == id);
            }
            return false;
        }
        finally { HidNative.HidD_FreePreparsedData(data); }
    }
}
