using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PowerHub.Models;

namespace PowerHub.Services;

public sealed record DiagnosticReport(string Json, string Fingerprint, int FailedDeviceCount);

public static class DiagnosticReportBuilder
{
    private const int MaximumBytes = 50 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static bool IsBatteryReadFailure(DeviceStatus device)
    {
        if (!device.ConnectionKnown || !device.IsConnected || device.BatteryPercent is >= 0 and <= 100
            || BatteryLevel(device.BatteryLevel) is { } level && level != "未知" || device.PowerSource == "USB 供电"
            || Status(device.Message) == "offline") return false;
        // A generic wired HID without a battery is not a read failure. Names are only
        // local hints for otherwise unadapted receivers and never enter the report.
        return device.HasProtocolConnectionState || Connection(device.Connection) is "bluetooth" or "receiver"
            || device.Name.Contains("receiver", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("dongle", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("接收器", StringComparison.Ordinal);
    }

    // This is a separate, allowlisted public report. The local diagnostic export may contain
    // serials, device paths and names; none of those free-form strings are copied here.
    public static DiagnosticReport Build(ScanResult scan, string applicationVersion)
    {
        var version = Regex.IsMatch(applicationVersion, @"^\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9.-]{1,32})?$")
            ? applicationVersion : "unknown";
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Alias(string identity)
        {
            if (!aliases.TryGetValue(identity, out var alias)) aliases[identity] = alias = $"device-{aliases.Count + 1}";
            return alias;
        }
        string DeviceAlias(DeviceStatus device) => Alias(device.ContainerId is { Length: > 0 } container
            ? "container:" + container : "id:" + device.Id);
        string? ReceiverAlias(DeviceStatus device) => device.ReceiverContainerId is { Length: > 0 } receiver
            ? Alias("container:" + receiver) : null;

        var selected = scan.Devices.OrderByDescending(IsBatteryReadFailure).Take(64).ToArray();
        var devices = selected.Select(device => new PublicDevice(
            DeviceAlias(device), ReceiverAlias(device), Hex(device.VendorId), Hex(device.ProductId),
            Known(device.Category, "键盘", "鼠标", "耳机", "麦克风", "音频设备", "手柄", "外设"),
            Connection(device.Connection), device.ConnectionKnown, device.IsConnected,
            device.BatteryPercent is >= 0 and <= 100 ? device.BatteryPercent : null,
            BatteryLevel(device.BatteryLevel), device.IsCharging,
            device.HasProtocolConnectionState, Protocol(device.Source), Status(device.Message), IsBatteryReadFailure(device))).ToList();

        var interfaces = new List<PublicInterface>();
        foreach (var line in scan.Diagnostics.Take(2048))
        {
            if (line.Length > 16384 || !line.StartsWith("HID ", StringComparison.Ordinal)) continue;
            var separator = line.IndexOf(';');
            var instance = (separator >= 0 ? line[4..separator] : line[4..].Split(": ", 2)[0]).Trim();
            var vid = Field(line, @";\s*VID=([0-9A-F]{4})(?=;|$)") ?? Field(instance, @"\bVID_([0-9A-F]{4})");
            var pid = Field(line, @";\s*PID=([0-9A-F]{4})(?=;|$)") ?? Field(instance, @"\bPID_([0-9A-F]{4})");
            if (vid is null || pid is null) continue;
            var container = Regex.Match(line, @"(?:^|;\s*)Container=([0-9A-F-]{36})(?=;|$)", RegexOptions.IgnoreCase);
            var exact = selected.FirstOrDefault(device => string.Equals(device.InstanceId, instance, StringComparison.OrdinalIgnoreCase));
            var candidates = selected.Where(device => Hex(device.VendorId) == vid && Hex(device.ProductId) == pid)
                .Select(device => ReceiverAlias(device) ?? DeviceAlias(device)).Distinct().ToArray();
            var alias = container.Success && Guid.TryParse(container.Groups[1].Value, out var guid)
                ? Alias("container:" + guid.ToString("D"))
                : exact is not null ? ReceiverAlias(exact) ?? DeviceAlias(exact)
                : candidates.Length == 1 ? candidates[0] : Alias("interface:" + instance);
            // Parse only descriptor and documented battery-query fields. In particular, never
            // retain the Serial= field, instance suffix, exception text or optional name replies.
            var protocol = Protocol(line);
            var queries = protocol == "Logitech HID++" && vid == "046D" ? BatteryQueries(line) : [];
            interfaces.Add(new PublicInterface(alias, vid, pid,
                Field(instance, @"&MI_([0-9A-F]{2})"), Field(instance, @"&COL([0-9A-F]{2})"),
                Field(line, @";\s*Usage=([0-9A-F]{4}/[0-9A-F]{4})"),
                Number(line, "InputBytes"), Number(line, "OutputBytes"), Number(line, "FeatureBytes"),
                Reports(line, "InputReports"), Reports(line, "OutputReports"), Reports(line, "FeatureReports"),
                protocol, Status(line), Error(line), queries.Length > 0 ? queries : null, BatteryPrefix(line, protocol)));
        }
        interfaces = interfaces.OrderByDescending(item => item.Protocol != "unknown").Take(128).ToList();

        string json;
        while (true)
        {
            var included = devices.Select(device => device.Id).ToHashSet();
            var references = devices.Select(device => device.ReceiverId).Concat(interfaces.Select(item => item.DeviceId))
                .Where(alias => alias is not null && !included.Contains(alias)).Distinct().Select(alias => new { Id = alias }).ToArray();
            json = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, ApplicationVersion = version, WindowsVersion = Environment.OSVersion.Version.ToString(),
                FailedDeviceCount = scan.Devices.Count(IsBatteryReadFailure), Devices = devices, RelatedDevices = references, Interfaces = interfaces,
                OmittedDevices = scan.Devices.Count - devices.Count,
                OmittedDiagnosticLines = scan.Diagnostics.Count - interfaces.Count
            }, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) <= MaximumBytes) break;
            if (interfaces.Count > 0) interfaces.RemoveAt(interfaces.Count - 1);
            else devices.RemoveAt(devices.Count - 1);
        }
        var failures = devices.Where(device => device.BatteryReadFailed).ToArray();
        var mode = failures.Length == 0 ? "manual" : "failure";
        var focus = failures.Length == 0 ? devices : failures.AsEnumerable();
        var keys = focus.Select(device => string.Join('|', device.VendorId ?? "-", device.ProductId ?? "-", device.Category ?? "-",
            device.Connection, device.Protocol, device.Status, device.BatteryPercent.HasValue ? "percent" : device.BatteryLevel is not (null or "未知") ? "level" : "none"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("1\n" + version + "\n" + mode + "\n" + string.Join("\n", keys)))).ToLowerInvariant();
        return new(json, fingerprint, scan.Devices.Count(IsBatteryReadFailure));
    }

    private static string? Hex(int? value) => value is >= 0 and <= 0xFFFF ? value.Value.ToString("X4") : null;
    private static string? Known(string? value, params string[] allowed) => allowed.Contains(value) ? value : null;
    private static string? BatteryLevel(string? value)
    {
        if (value is "极低" or "低" or "中" or "高" or "满" or "未知" or "已耗尽" or "低电量" or "中等" or "充足" or "满电") return value;
        if (value is not null && Regex.IsMatch(value, @"^档位 [0-4]/4$")) return value;
        if (value is not null && Regex.IsMatch(value, @"^(?:0|[1-9]0)–(?:9|[1-9]9)%$"))
        {
            var range = value.TrimEnd('%').Split('–');
            if (int.Parse(range[1]) == int.Parse(range[0]) + 9) return value;
        }
        return null;
    }
    private static string? Field(string line, string pattern) => Value(Regex.Match(line, pattern, RegexOptions.IgnoreCase), 1);
    private static string? Value(Match match, int group) => match.Success && match.Groups[group].Success ? match.Groups[group].Value.ToUpperInvariant() : null;
    private static int? Number(string line, string field) => int.TryParse(Field(line, @";\s*" + field + @"=(\d{1,5})(?=;|$)"), out var value) && value <= 65535 ? value : null;
    private static string[]? Reports(string line, string field)
    {
        var value = Field(line, @";\s*" + field + @"=([^;]*)");
        if (value is null) return null;
        return Regex.Matches(value, @"(?:^|,)([0-9A-F]{2}:(?:\d{1,3}X\d{1,5}|BUTTONS))(?=,|$)")
            .Take(32).Select(match => match.Groups[1].Value).ToArray();
    }
    private static int? Error(string line) => int.TryParse(Field(line, @"(?:Windows 错误 |打开失败 |描述符读取失败 )(\d{1,5})(?!\d)"), out var code) ? code : null;
    private static string Connection(string value) => value.StartsWith("蓝牙", StringComparison.Ordinal) || value.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("BLE", StringComparison.OrdinalIgnoreCase) ? "bluetooth"
        : value.Contains("2.4G", StringComparison.OrdinalIgnoreCase) || value.Contains("接收器", StringComparison.Ordinal)
            || value.Contains("receiver", StringComparison.OrdinalIgnoreCase) ? "receiver"
        : value.Contains("USB", StringComparison.OrdinalIgnoreCase) ? "usb" : "unknown";
    private static string Protocol(string value)
    {
        foreach (var name in new[] { "Logitech HID++", "MCHOSE", "LANGTU LT95", "LANGTU Shinetek", "Weisheng", "Akko", "Razer", "Compx", "AULA", "DualSense", "Steam", "SteelSeries Nova", "Corsair VOID", "HyperX Cloud", "HID 标准电池", "BLE Battery Service" })
            if (value.Contains(name, StringComparison.OrdinalIgnoreCase)) return name;
        return "unknown";
    }
    private static string Status(string value)
    {
        var error = Regex.Match(value, @"设备错误 ([0-9A-F]{2})(?![0-9A-F])", RegexOptions.IgnoreCase);
        if (error.Success) return "device_error_" + error.Groups[1].Value.ToUpperInvariant();
        if (value.Contains("未发送查询", StringComparison.Ordinal) || value.Contains("描述符与电量协议不匹配", StringComparison.Ordinal)) return "descriptor_mismatch";
        if (value.Contains("缺少安全定位", StringComparison.Ordinal) || value.Contains("没有可读取", StringComparison.Ordinal)) return "missing_collection";
        if (value.Contains("无法打开", StringComparison.Ordinal) || value.Contains("打开失败", StringComparison.Ordinal) || value.Contains("接口不可用", StringComparison.Ordinal)) return "access_failed";
        if (value.Contains("未通过校验", StringComparison.Ordinal) || value.Contains("未采纳", StringComparison.Ordinal) || value.Contains("超出有效范围", StringComparison.Ordinal)) return "invalid_response";
        if (value.Contains("超时", StringComparison.Ordinal) || value.Contains("查询超过", StringComparison.Ordinal) || value.Contains("预算用尽", StringComparison.Ordinal)) return "timeout";
        if (value.Contains("无匹配响应", StringComparison.Ordinal) || value.Contains("未收到", StringComparison.Ordinal)) return "no_response";
        if (value.Contains("报告耳机休眠", StringComparison.Ordinal) || value.Contains("报告外设离线", StringComparison.Ordinal)
            || value.Contains("报告鼠标离线", StringComparison.Ordinal) || value.Contains("已断开", StringComparison.Ordinal)
            || value.Contains("设备已离线", StringComparison.Ordinal)) return "offline";
        if (value.Contains("无标准电池", StringComparison.Ordinal) || value.Contains("未声明可查询", StringComparison.Ordinal)) return "no_standard_battery";
        if (value.Contains("已读取", StringComparison.Ordinal) || value.Contains("已返回电量", StringComparison.Ordinal)) return "success";
        return "unknown";
    }
    private static BatteryQuery[] BatteryQueries(string line)
    {
        var features = new Dictionary<(int Slot, string Feature), string>();
        var queries = new List<BatteryQuery>();
        const string pattern = @"(?:^|;\s*)slot=([1-6]) feature=([0-9A-F]{2}) fn=([01]) args=([0-9A-F]{4}): (?:无匹配响应|设备错误 ([0-9A-F]{2}) \(([0-9A-F]{14})\)|([0-9A-F]{14}))(?=;|$)";
        foreach (Match match in Regex.Matches(line, pattern, RegexOptions.IgnoreCase).Take(128))
        {
            var slot = int.Parse(match.Groups[1].Value);
            var feature = Value(match, 2)!;
            var function = Value(match, 3)!;
            var arguments = Value(match, 4)!;
            var discovery = feature == "00" && function == "0" && arguments is "1004" or "1000";
            if (!discovery && !(arguments == "0000" && features.TryGetValue((slot, feature), out var batteryFeature)
                && (function == "0" || batteryFeature == "1004"))) continue;
            var error = Value(match, 5);
            var response = Value(match, 6) ?? Value(match, 7);
            if (response is not null)
            {
                var bytes = Convert.FromHexString(response);
                var requestedFeature = Convert.ToByte(feature, 16);
                var requestedFunction = int.Parse(function);
                if (bytes[1] != slot) continue;
                if (error is not null)
                {
                    if (!(bytes[0] == 0x10 && bytes[2] == 0x8F || bytes[0] == 0x11 && bytes[2] == 0xFF)
                        || bytes[3] != requestedFeature || bytes[4] >> 4 != requestedFunction || (bytes[4] & 0x0F) == 0
                        || bytes[5] != Convert.ToByte(error, 16) || bytes[6] != 0) continue;
                }
                else
                {
                    if (bytes[0] is not (0x10 or 0x11) || bytes[2] != requestedFeature
                        || bytes[3] >> 4 != requestedFunction || (bytes[3] & 0x0F) == 0) continue;
                    if (discovery && bytes[4] is > 0 and < 0xFF) features[(slot, bytes[4].ToString("X2"))] = arguments;
                }
            }
            queries.Add(new BatteryQuery(slot, feature, function, arguments, error, response));
            if (queries.Count == 40) break;
        }
        return queries.ToArray();
    }
    private static string? BatteryPrefix(string line, string protocol)
    {
        var prefix = Field(line, @"前缀=([0-9A-F]{2,20})(?![0-9A-F])");
        if (prefix is null) return null;
        if (prefix.Length % 2 != 0) return null;
        var bytes = Convert.FromHexString(prefix);
        if (protocol == "MCHOSE" && bytes.Length >= 4 && bytes[0] == 0x55 && bytes[1] == 0x65) return prefix[..8];
        if (protocol == "MCHOSE" && bytes.Length == 10 && bytes[0] == 0x55 && bytes[1] == 0xAA && bytes[2] == 1
            && bytes[3] <= 1 && bytes[4] == 3 && bytes[5] == 0 && bytes[6] == 1 && bytes[7] == 0x0B) return prefix;
        if (protocol == "LANGTU LT95" && bytes.Length >= 5 && prefix.StartsWith("00200100", StringComparison.Ordinal)) return prefix[..10];
        if (protocol == "Akko" && bytes.Length == 10 && prefix.StartsWith("0400001A06000000", StringComparison.Ordinal)) return prefix;
        return null;
    }

    private sealed record PublicDevice(string Id, string? ReceiverId, string? VendorId, string? ProductId, string? Category,
        string Connection, bool ConnectionKnown, bool IsConnected, int? BatteryPercent, string? BatteryLevel, bool? IsCharging,
        bool HasProtocolConnectionState, string Protocol, string Status, bool BatteryReadFailed);
    private sealed record PublicInterface(string DeviceId, string VendorId, string ProductId, string? Interface, string? Collection,
        string? Usage, int? InputBytes, int? OutputBytes, int? FeatureBytes, string[]? InputReports, string[]? OutputReports,
        string[]? FeatureReports, string Protocol, string Status, int? WindowsError, BatteryQuery[]? BatteryQueries, string? BatteryPrefix);
    private sealed record BatteryQuery(int Slot, string Feature, string Function, string Arguments, string? Error, string? Response);
}
