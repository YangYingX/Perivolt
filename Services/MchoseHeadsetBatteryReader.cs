using PowerHub.Models;
using PowerHub.Native;

namespace PowerHub.Services;

internal static class MchoseHeadsetBatteryReader
{
    // Family identifiers and battery commands: MCHOSE's public M HUB driver.
    // Independent V9 Pro hardware capture: rafagfran/mchose-v9-pro-battery-tray (MIT).
    // Protocol facts and source snapshots are documented in docs/protocol-families.md.
    internal enum Protocol { Legacy, Framed }

    internal static Protocol? Match(DeviceStatus device, HidNative.Caps caps)
    {
        if (caps.UsagePage < 0xFF00 || caps.InputReportByteLength != 64 || caps.OutputReportByteLength != 64) return null;
        return (device.VendorId, device.ProductId) switch
        {
            (0x291D, 0x385D) when caps.UsagePage == 0xFF90 && caps.Usage == 1 => Protocol.Legacy,
            (0x3837, 0x6045 or 0x6008) => Protocol.Legacy,
            (0x3837, 0x6011 or 0x6013) => Protocol.Framed,
            _ => null
        };
    }

    internal static DeviceStatus? TryRead(string path, DeviceStatus device, HidNative.Caps caps, out string detail)
    {
        detail = "";
        var protocol = Match(device, caps);
        if (protocol is null) return null;
        var result = HidBatteryQuery.Read(path, caps, CreatePayload(protocol.Value), 0x55, payload =>
        {
            if (!TryParse(payload, protocol.Value, out var percent, out var charging, out var online)) return null;
            return device with
            {
                Category = "耳机", Connection = "2.4G", ConnectionKnown = true, IsConnected = online,
                BatteryPercent = online ? percent : null, BatteryLevel = null, IsCharging = online ? charging : null, IsBatteryLow = null,
                UpdatedAt = online ? DateTimeOffset.Now : null, Source = "MCHOSE 耳机系列电量协议",
                Message = online ? "已读取耳机电量与充电状态" : "接收器报告耳机休眠，已清除电量"
            };
        }, out var status);
        detail = $"MCHOSE {protocol}; VID={device.VendorId:X4}; PID={device.ProductId:X4}; {status}";
        return result ?? device with
        {
            Category = "耳机", Connection = "2.4G", ConnectionKnown = false, IsConnected = false,
            BatteryPercent = null, BatteryLevel = null, IsCharging = null, IsBatteryLow = null, UpdatedAt = null,
            Source = "MCHOSE 耳机系列电量协议", Message = status
        };
    }

    internal static byte[] CreatePayload(Protocol protocol)
    {
        var payload = new byte[63];
        if (protocol == Protocol.Legacy) { payload[0] = 0x65; payload[1] = 1; }
        else
        {
            payload[0] = 0xAA; payload[1] = 1; payload[3] = 1;
            payload[5] = 1; payload[6] = 0x0B;
        }
        return payload;
    }

    internal static bool TryParse(ReadOnlySpan<byte> payload, Protocol protocol,
        out int percent, out bool charging, out bool online)
    {
        percent = 0; charging = false; online = false;
        if (payload.Length != 63) return false;
        if (protocol == Protocol.Legacy)
        {
            if (payload[0] != 0x65 || payload[2] is not (2 or 3 or 4 or 0x1A)) return false;
            online = payload[2] != 0x1A;
            if (online && payload[1] > 100) return false;
            percent = online ? payload[1] : 0;
            charging = online && payload[2] == 3;
        }
        else
        {
            if (payload[0] != 0xAA || payload[1] != 1 || payload[2] > 1 || payload[3] != 3
                || payload[4] != 0 || payload[5] != 1 || payload[6] != 0x0B) return false;
            byte checksum = 0;
            if (payload[2] == 1)
                for (var index = 2; index <= 8; index++) checksum ^= payload[index];
            if (payload[9] != checksum) return false;
            online = payload[7] != 0;
            percent = online ? payload[8] & 0x7F : 0;
            if (percent > 100) return false;
            charging = online && (payload[8] & 0x80) != 0;
        }
        return true;
    }
}
