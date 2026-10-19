namespace PowerHub.Models;

public sealed record DeviceStatus
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "未知设备";
    public string Manufacturer { get; init; } = "";
    public string Category { get; init; } = "外设";
    public string Connection { get; init; } = "未知";
    public bool IsConnected { get; init; }
    public bool ConnectionKnown { get; init; } = true;
    public int? BatteryPercent { get; init; }
    public string? BatteryLevel { get; init; }
    public bool? IsBatteryLow { get; init; }
    public string? PowerSource { get; init; }
    public bool? IsCharging { get; init; }
    public string Source { get; init; } = "设备枚举";
    public string Message { get; init; } = "设备未提供可读取的电量";
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? ContainerId { get; init; }
    public string? ReceiverContainerId { get; init; }
    public bool HasProtocolConnectionState { get; init; }
    public string? InstanceId { get; init; }
    public string? BluetoothAddress { get; init; }
    public int? VendorId { get; init; }
    public int? ProductId { get; init; }
    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    public bool HasBattery => BatteryPercent.HasValue || BatteryLevel is not null;
    public string BatteryText => BatteryPercent is int value ? $"{value}%" : BatteryLevel ?? PowerSource ?? "--";
    public string ChargingText => IsCharging switch { true => "正在充电", false => "未充电", _ => "充电状态未知" };
    public string ConnectionText => !ConnectionKnown ? "连接未知" : IsConnected ? "已连接" : "未连接";
    public string UpdatedText => UpdatedAt is { } time ? time.ToLocalTime().ToString("HH:mm:ss") : "尚未读取";
    public string HardwareText => VendorId is int vendor && ProductId is int product ? $"VID {vendor:X4} · PID {product:X4}" : "无 USB 标识";
    public string Icon => Category switch { "键盘" => "⌨", "鼠标" => "◉", "耳机" or "麦克风" or "音频设备" => "♫", "手柄" => "✚", _ => "◇" };
}

public sealed record ScanResult(IReadOnlyList<DeviceStatus> Devices, IReadOnlyList<string> Diagnostics, DateTimeOffset ScannedAt);
