using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PowerHub.Models;

namespace PowerHub.Services;

/// <summary>Reads current macOS system properties without opening devices or changing their configuration.</summary>
public sealed class MacBatteryProvider
{
    private readonly SemaphoreSlim scanGate = new(1, 1);

    public async Task<ScanResult> ScanAsync(bool includeBle = true, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("设备扫描需要在 macOS 上运行。");

        await scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedAt = DateTimeOffset.Now;
            // The commands are independent. Always consume both tasks, including on cancellation.
            var hidTask = CaptureAsync("/usr/sbin/ioreg", ["-a", "-r", "-c", "IOHIDDevice"],
                TimeSpan.FromSeconds(12), cancellationToken);
            var bluetoothTask = includeBle
                ? CaptureAsync("/usr/sbin/system_profiler", ["SPBluetoothDataType", "-json", "-detailLevel", "full"],
                    TimeSpan.FromSeconds(25), cancellationToken)
                : Task.FromResult(new CommandOutput("", null));
            await Task.WhenAll(hidTask, bluetoothTask).ConfigureAwait(false);
            var diagnostics = new List<string>();
            var readings = new List<DeviceStatus>();
            AddReadings(await hidTask.ConfigureAwait(false), "IORegistry", text => ParseHidRegistry(text, startedAt), readings, diagnostics);
            if (includeBle)
                AddReadings(await bluetoothTask.ConfigureAwait(false), "Bluetooth", text => ParseBluetoothProfile(text, startedAt), readings, diagnostics);
            var devices = MergeDevices(readings).Where(device => device.ConnectionKnown && device.IsConnected).ToArray();
            diagnostics.Add($"macOS：当前确认连接 {devices.Length} 台外设，{devices.Count(device => device.HasBattery)} 台提供电量。");
            diagnostics.Add("时间表示本次系统属性采样，不代表设备刚刚刷新电量；系统属性可能包含缓存，未提供的电量和充电状态保持未知。");
            diagnostics.Add(includeBle
                ? "蓝牙仅采用 system_profiler 当前已连接列表；未主动连接离线设备或查询 BLE GATT。"
                : "本次已跳过蓝牙系统信息扫描，不确认 IORegistry 中的蓝牙连接。");
            diagnostics.Add("macOS 版本尚未移植 Windows 厂商私有 HID 查询或 XInput；接收器存在不能证明无线外设在线。");
            return new ScanResult(devices, diagnostics, DateTimeOffset.Now);
        }
        finally { scanGate.Release(); }
    }

    private static void AddReadings(CommandOutput output, string source,
        Func<string, IReadOnlyList<DeviceStatus>> parse, List<DeviceStatus> readings, List<string> diagnostics)
    {
        if (output.Error is not null) { diagnostics.Add($"{source} 读取失败：{output.Error}"); return; }
        try { readings.AddRange(parse(output.Text)); }
        catch (Exception error) when (error is XmlException or JsonException or FormatException or InvalidOperationException)
        { diagnostics.Add($"{source} 数据格式无法识别：{error.Message}"); }
    }

    private static async Task<CommandOutput> CaptureAsync(string executable, string[] arguments,
        TimeSpan limit, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["LC_ALL"] = "C";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            // Read concurrently to avoid filling either redirected pipe.
            var output = ReadLimitedAsync(process.StandardOutput, timeout.Token);
            var error = ReadLimitedAsync(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                var stderr = await error.ConfigureAwait(false);
                return process.ExitCode == 0 ? new(text, null)
                    : new("", $"系统命令退出码 {process.ExitCode}；{CleanError(stderr)}");
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception errorDuringKill) when (errorDuringKill is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                timeout.Cancel();
                // Observe both redirected reads before disposing their process handles.
                try { await Task.WhenAll(output, error).ConfigureAwait(false); }
                catch (Exception readError) when (readError is OperationCanceledException or IOException) { }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new("", $"系统命令超过 {limit.TotalSeconds:0} 秒，已取消。"); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new("", CleanError(error.Message)); }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 16 * 1024 * 1024;
        var builder = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return builder.ToString();
            if (builder.Length + count > maximumCharacters) throw new IOException("系统信息超过 16 MiB，已停止读取。");
            builder.Append(buffer, 0, count);
        }
    }

    private static string CleanError(string error) => error.Trim().Length > 300 ? error.Trim()[..300] : error.Trim();
    private sealed record CommandOutput(string Text, string? Error);

    internal static IReadOnlyList<DeviceStatus> ParseHidRegistry(string xml, DateTimeOffset scannedAt)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        var devices = new List<DeviceStatus>();
        foreach (var dictionary in document.Descendants("dict"))
        {
            var fields = PlistFields(dictionary);
            var transport = Text(fields, "Transport");
            // Only actual HID device roots expose transport. Child element dictionaries do not qualify.
            if (transport.Length == 0 || Flag(fields, "Built-In", "BuiltIn", "AppleEmbeddedKeyboard") == true) continue;
            var serviceClass = Text(fields, "IOClass");
            if (transport.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                || serviceClass.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                || transport is "SPI" or "I2C" or "FIFO"
                || Flag(fields, "VirtualHIDDevice", "IsVirtual") == true) continue;
            var bluetooth = transport.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
            if (!bluetooth && !transport.Equals("USB", StringComparison.OrdinalIgnoreCase)) continue;
            var connected = Flag(fields, "Connected", "IsConnected", "DeviceConnected");
            if (connected == false) continue;
            var name = Text(fields, "Product", "ProductName", "USB Product Name");
            if (name.Length == 0) name = "外接 HID 设备";
            if (name.Contains("Apple Internal", StringComparison.OrdinalIgnoreCase)) continue;
            var vendor = ValidUsbId(Number(fields, "VendorID", "idVendor"));
            var product = ValidUsbId(Number(fields, "ProductID", "idProduct"));
            var serial = Text(fields, "SerialNumber", "USB Serial Number");
            var address = NormalizeAddress(Text(fields, "DeviceAddress", "BluetoothAddress", "BD_ADDR"));
            var registryId = Text(fields, "IORegistryEntryID");
            var location = Text(fields, "LocationID", "locationID");
            string id;
            if (address is not null) id = "bluetooth:" + address;
            else if (serial.Length > 0 && vendor is not null && product is not null)
                id = $"mac-hid:{vendor:X4}:{product:X4}:serial:{IdentityHash(serial)}";
            else if (location.Length > 0 && vendor is not null && product is not null)
                id = $"mac-hid:{vendor:X4}:{product:X4}:port:{location}";
            else if (registryId.Length > 0) id = "mac-hid:registry:" + registryId;
            else continue; // Never create a name-based identity that can merge different physical devices.
            var battery = Percent(Text(fields, "BatteryPercent", "BatteryPercentage", "BatteryLevel"));
            var charging = Flag(fields, "IsCharging", "BatteryIsCharging", "Charging");
            var low = Flag(fields, "BatteryLow", "IsBatteryLow");
            var receiver = name.Contains("receiver", StringComparison.OrdinalIgnoreCase)
                || name.Contains("dongle", StringComparison.OrdinalIgnoreCase) || name.Contains("接收器", StringComparison.Ordinal);
            devices.Add(new DeviceStatus
            {
                Id = id, ContainerId = id, InstanceId = "ioreg:" + registryId,
                Name = name, Manufacturer = Text(fields, "Manufacturer"),
                Category = Category(name, Number(fields, "PrimaryUsagePage"), Number(fields, "PrimaryUsage")),
                Connection = bluetooth ? "蓝牙" : receiver ? "2.4G 接收器" : "USB",
                // A Bluetooth IORegistry node alone can outlive a connection; require the system's connected list.
                ConnectionKnown = !bluetooth, IsConnected = !bluetooth,
                BluetoothAddress = address, VendorId = vendor, ProductId = product,
                BatteryPercent = battery, IsCharging = charging, IsBatteryLow = low,
                Source = "macOS IORegistry", UpdatedAt = battery is not null || charging is not null ? scannedAt : null,
                Message = receiver ? "接收器已连接；无线外设在线状态未知" : battery is not null
                    ? "本次采样的 macOS 系统电量属性，设备刷新时间未知，可能包含驱动缓存" : "设备未提供可读取的系统电量属性"
            });
        }
        return devices;
    }

    internal static IReadOnlyList<DeviceStatus> ParseBluetoothProfile(string json, DateTimeOffset scannedAt)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var result = new List<DeviceStatus>();
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            { foreach (var item in element.EnumerateArray()) Visit(item); return; }
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (var property in element.EnumerateObject())
            {
                // Do not walk device_not_connected, paired devices or controller metadata as current devices.
                if (property.NameEquals("device_connected") && property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    ConnectedEntries(property.Value);
                else if (!property.NameEquals("device_not_connected")) Visit(property.Value);
            }
        }
        void ConnectedEntries(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            { foreach (var item in element.EnumerateArray()) ConnectedEntries(item); return; }
            if (element.ValueKind != JsonValueKind.Object) return;
            if (element.TryGetProperty("device_address", out _)) { AddDevice(JsonText(element, "_name", "device_title"), element); return; }
            foreach (var entry in element.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Object) AddDevice(entry.Name, entry.Value);
        }
        void AddDevice(string name, JsonElement fields)
        {
            var address = NormalizeAddress(JsonText(fields, "device_address"));
            if (address is null) return; // No safe identity is available.
            var connection = JsonText(fields, "device_connected");
            if (ParseFlag(connection) == false) return;
            if (string.IsNullOrWhiteSpace(name)) name = "蓝牙外设";
            var id = "bluetooth:" + address;
            var battery = Percent(JsonText(fields, "device_batteryLevelMain", "device_batteryLevel", "device_battery_level"));
            var chargingText = JsonText(fields, "device_isCharging", "device_is_charging");
            var charging = ParseFlag(chargingText);
            var category = Category(name + " " + JsonText(fields, "device_minorType", "device_minor_type"), null, null);
            var device = new DeviceStatus
            {
                Id = id, ContainerId = id, BluetoothAddress = address, Name = name,
                Manufacturer = JsonText(fields, "device_manufacturer"), Category = category,
                Connection = "蓝牙", IsConnected = true, ConnectionKnown = true,
                BatteryPercent = battery, IsCharging = charging,
                VendorId = ValidUsbId(ParseNumber(JsonText(fields, "device_vendorID"))),
                ProductId = ValidUsbId(ParseNumber(JsonText(fields, "device_productID"))),
                Source = "macOS Bluetooth", UpdatedAt = battery is not null || charging is not null ? scannedAt : null,
                Message = battery is not null ? "本次采样的已连接蓝牙设备电量，设备刷新时间未知，可能包含系统缓存" : "已确认蓝牙连接；系统未提供电量"
            };
            var components = new[]
            {
                (Key: "device_batteryLevelLeft", Suffix: "left", Label: "左耳"),
                (Key: "device_batteryLevelRight", Suffix: "right", Label: "右耳"),
                (Key: "device_batteryLevelCase", Suffix: "case", Label: "充电盒")
            }.Select(part => (part, percent: Percent(JsonText(fields, part.Key))))
                .Where(part => part.percent is not null).ToArray();
            if (battery is not null || components.Length == 0) result.Add(device);
            foreach (var (part, percent) in components)
                result.Add(device with
                {
                    Id = id + ":" + part.Suffix, ContainerId = id + ":" + part.Suffix,
                    // Component addresses stay absent so their readings are never merged into one percentage.
                    BluetoothAddress = null, Name = name + " · " + part.Label, Category = "耳机",
                    BatteryPercent = percent, IsCharging = null, UpdatedAt = scannedAt,
                    Message = "本次采样的分部电量，设备刷新时间未知；充电盒自身连接与充电状态未知"
                });
        }
        Visit(document.RootElement);
        return result;
    }

    internal static IReadOnlyList<DeviceStatus> MergeDevices(IEnumerable<DeviceStatus> readings)
    {
        var samples = readings.ToArray();
        var connectedBluetooth = samples.Where(device => device.Source == "macOS Bluetooth" && device.ConnectionKnown && device.IsConnected)
            .Select(device => BluetoothRootId(device.Id)).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var confirmed = samples.Where(device => device.Connection != "蓝牙" ||
            BluetoothRootId(device.Id) is { } id && connectedBluetooth.Contains(id));
        // Parsers encode a physical identity; display names and VID/PID alone are never merge keys.
        return confirmed.GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var members = group.ToArray();
            var connected = members.FirstOrDefault(device => device.ConnectionKnown && device.IsConnected) ?? members[0];
            var battery = members.Where(device => device.BatteryPercent is >= 0 and <= 100)
                .OrderByDescending(device => device.Source == "macOS Bluetooth").FirstOrDefault();
            var charging = members.Where(device => device.IsCharging is not null)
                .OrderByDescending(device => device.Source == "macOS Bluetooth").FirstOrDefault();
            var hardware = members.FirstOrDefault(device => device.VendorId is not null);
            var name = members.FirstOrDefault(device => device.Source == "macOS Bluetooth") ?? members[0];
            return connected with
            {
                ConnectionKnown = connected.Connection == "蓝牙" || connected.ConnectionKnown,
                IsConnected = connected.Connection == "蓝牙" || connected.IsConnected,
                Name = name.Name, Category = name.Category != "外设" ? name.Category : connected.Category,
                BatteryPercent = battery?.BatteryPercent, IsCharging = charging?.IsCharging,
                IsBatteryLow = members.FirstOrDefault(device => device.IsBatteryLow is not null)?.IsBatteryLow,
                Source = battery?.Source ?? connected.Source, UpdatedAt = battery?.UpdatedAt ?? charging?.UpdatedAt,
                Message = battery?.Message ?? connected.Message,
                Manufacturer = members.Select(device => device.Manufacturer).FirstOrDefault(value => value.Length > 0) ?? "",
                VendorId = hardware?.VendorId, ProductId = hardware?.ProductId
            };
        }).OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string? BluetoothRootId(string id) => id.Length >= 22 && id.StartsWith("bluetooth:", StringComparison.OrdinalIgnoreCase)
        && id.AsSpan(10, 12).ToString().All(Uri.IsHexDigit) && (id.Length == 22 || id[22] == ':') ? id[..22] : null;

    private static Dictionary<string, XElement> PlistFields(XElement dictionary)
    {
        var fields = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var children = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < children.Length; index++)
            if (children[index].Name.LocalName == "key") fields[children[index].Value] = children[++index];
        return fields;
    }

    private static string Text(Dictionary<string, XElement> fields, params string[] keys)
    {
        foreach (var key in keys)
            if (fields.TryGetValue(key, out var value))
            {
                if (value.Name.LocalName is "true" or "false") return value.Name.LocalName;
                if (value.Name.LocalName is "integer" or "string" or "real") return value.Value.Trim();
            }
        return "";
    }

    private static int? Number(Dictionary<string, XElement> fields, params string[] keys) => ParseNumber(Text(fields, keys));
    private static bool? Flag(Dictionary<string, XElement> fields, params string[] keys) => ParseFlag(Text(fields, keys));
    private static bool? ParseFlag(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "attrib_yes" => true,
        "false" or "no" or "0" or "attrib_no" => false,
        _ => null
    };
    private static int? ParseNumber(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null
        : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static int? ValidUsbId(int? value) => value is >= 0 and <= ushort.MaxValue ? value : null;
    private static int? Percent(string value)
    {
        value = value.Trim();
        if (value.EndsWith('%')) value = value[..^1].Trim();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
            && percent is >= 0 and <= 100 ? percent : null;
    }
    private static string JsonText(JsonElement fields, params string[] keys)
    {
        foreach (var key in keys)
            if (fields.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString().Trim();
        return "";
    }
    private static string? NormalizeAddress(string address)
    {
        var normalized = address.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim();
        return normalized.Length == 12 && normalized.All(Uri.IsHexDigit) ? normalized.ToUpperInvariant() : null;
    }
    private static string IdentityHash(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    private static string Category(string name, int? page, int? usage)
    {
        if (page == 1)
        {
            if (usage == 2) return "鼠标";
            if (usage == 6) return "键盘";
            if (usage is 4 or 5) return "手柄";
        }
        var text = name.ToLowerInvariant();
        if (text.Contains("keyboard") || text.Contains("键盘")) return "键盘";
        if (text.Contains("mouse") || text.Contains("trackpad") || text.Contains("鼠标")) return "鼠标";
        if (text.Contains("head") || text.Contains("airpod") || text.Contains("earbud") || text.Contains("耳机")) return "耳机";
        if (text.Contains("microphone") || text.Contains("麦克风")) return "麦克风";
        if (text.Contains("controller") || text.Contains("gamepad") || text.Contains("手柄")) return "手柄";
        if (text.Contains("audio") || text.Contains("speaker")) return "音频设备";
        return "外设";
    }
}
