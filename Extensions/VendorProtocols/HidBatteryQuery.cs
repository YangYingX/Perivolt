using System.Diagnostics;
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
        byte? requiredReportId, ParseResponse parse, out string detail, int timeoutMilliseconds = 600,
        bool allowInputOnlyOutputReport = false, int? writeTimeoutMilliseconds = null)
    {
        detail = "未收到有效电量响应；外设可能未连接或处于休眠";
        string? rejectedReport = null;
        var writeCompleted = false;
        var writeMilliseconds = 0L;
        var inputReports = 0;
        try
        {
            using var handle = HidNative.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                detail = $"无法打开厂商接口，Windows 错误 {Marshal.GetLastWin32Error()}";
                return null;
            }
            byte inputId = 0, outputId = 0;
            // A known protocol report may be described as values or buttons.
            if (requiredReportId.HasValue) inputId = outputId = requiredReportId.Value;
            var reportsMatch = requiredReportId.HasValue
                ? DeclaresReport(handle, 0, inputId) && DeclaresReport(handle, 1, outputId, allowInputOnlyOutputReport)
                : TryGetReportIds(handle, out inputId, out outputId);
            if (!reportsMatch
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds + (writeTimeoutMilliseconds ?? 0)));
            using var writeTimeout = writeTimeoutMilliseconds is int writeLimit
                ? new CancellationTokenSource(TimeSpan.FromMilliseconds(writeLimit)) : null;
            var response = new byte[caps.InputReportByteLength];
            // The official client listens before sending. Keep the read alive through the write,
            // then give MCHOSE its full response budget after the write has completed.
            var firstRead = stream.ReadAsync(response.AsMemory(), timeout.Token).AsTask();
            try
            {
                var writeTimer = Stopwatch.StartNew();
                stream.WriteAsync(request.AsMemory(), writeTimeout?.Token ?? timeout.Token).AsTask().GetAwaiter().GetResult();
                writeMilliseconds = writeTimer.ElapsedMilliseconds;
                writeCompleted = true;
                if (writeTimeoutMilliseconds.HasValue) timeout.CancelAfter(timeoutMilliseconds);
                for (var packet = 0; packet < 12; packet++)
                {
                    var length = (packet == 0 ? firstRead : stream.ReadAsync(response.AsMemory(), timeout.Token).AsTask()).GetAwaiter().GetResult();
                    if (length == 0) break;
                    inputReports++;
                    var report = response.AsSpan(0, length);
                    rejectedReport = $"已收到未采纳的厂商报告；长度={length}；前缀={Convert.ToHexString(report[..Math.Min(length, 10)])}";
                    if (length != caps.InputReportByteLength || report[0] != inputId || report.SequenceEqual(request)) continue;
                    var reading = parse(report[1..]);
                    if (reading is null) continue;
                    detail = $"input={inputId:X2}; output={outputId:X2}; 写入完成耗时={writeMilliseconds}ms; 输入报告数={inputReports}; {reading.Message}";
                    return reading;
                }
            }
            finally
            {
                timeout.Cancel();
                // A failed write must not leave a read holding the report or handle.
                try
                {
                    var firstLength = firstRead.GetAwaiter().GetResult();
                    if (!writeCompleted && firstLength > 0)
                    {
                        inputReports++;
                        rejectedReport = $"写入未完成时收到厂商报告；长度={firstLength}；前缀={Convert.ToHexString(response.AsSpan(0, Math.Min(firstLength, 10)))}";
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) { detail = $"电量查询超过{(writeCompleted ? timeoutMilliseconds : writeTimeoutMilliseconds ?? timeoutMilliseconds)}毫秒；阶段={(writeCompleted ? "等待输入响应" : "写入查询")}；未确认外设在线"; }
        catch (IOException error) { detail = $"厂商电量查询失败：{error.Message}"; }
        catch (UnauthorizedAccessException error) { detail = $"厂商接口不可用：{error.Message}"; }
        detail += $"；写入={(writeCompleted ? $"已完成({writeMilliseconds}ms)" : "未完成")}；输入报告数={inputReports}；" +
            (rejectedReport ?? (writeCompleted ? "未收到输入报告" : "写入未完成，预挂输入读取已取消"));
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

    internal static bool DeclaresReport(SafeFileHandle handle, int type, byte id, bool allowInputOnlyOutputReport = false)
    {
        if (!HidNative.HidD_GetPreparsedData(handle, out var data)) return false;
        try
        {
            if (HidNative.HidP_GetCaps(data, out var caps) != HidNative.Success) return false;
            // V9 Turbo+ exposes OutputBytes=64 without output value/button caps on Windows.
            if (allowInputOnlyOutputReport && type == 1 && id == 0x55
                && caps.UsagePage == 0xFF90 && caps.Usage == 1
                && caps.InputReportByteLength == 64 && caps.OutputReportByteLength == 64
                && caps.NumberOutputValueCaps == 0 && caps.NumberOutputButtonCaps == 0
                && TryGetId(0, caps.NumberInputValueCaps, caps.InputReportByteLength, data, out var inputId) && inputId == id)
                return true;
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
