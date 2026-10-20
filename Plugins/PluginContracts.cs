using PowerHub.Models;

namespace PowerHub.Plugins;

public interface IPowerHubPlugin : IDisposable
{
    PluginOutput OnScan(PluginContext context) => new();
    PluginTheme? Theme => null;
    IReadOnlyList<PluginTheme> Themes => Theme is { } theme ? [theme] : [];
    HidPluginResult? ReadHid(HidDeviceContext device) => null;
    void IDisposable.Dispose() { }
}

public sealed record PluginContext(IReadOnlyList<DeviceStatus> Devices, DateTimeOffset Now, IReadOnlyDictionary<string, string> Options);
public sealed record PluginOutput(bool SuppressLowBatteryAlerts = false, IReadOnlyList<PluginNotification>? Notifications = null);
public sealed record PluginNotification(string Title, string Message);
public sealed record PluginTheme(string Id, bool IsDark, IReadOnlyDictionary<string, string> Colors)
{
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public string Description { get; init; } = "";
    public string Motif { get; init; } = "";
}
public sealed record PluginThemeEntry(string Value, string Name, string Group, PluginTheme Theme);
public sealed record HidDeviceContext(string Path, DeviceStatus Device, ushort UsagePage, ushort Usage,
    ushort InputReportByteLength, ushort OutputReportByteLength, ushort FeatureReportByteLength);
public sealed record HidPluginResult(DeviceStatus Device, string Detail, IReadOnlyList<DeviceStatus>? Children = null);

public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Assembly { get; set; } = "";
    public string EntryType { get; set; } = "";
    public string Kind { get; set; } = "utility";
    public int ApiVersion { get; set; } = 1;
    public int[] Vendors { get; set; } = [];
    public List<PluginOptionDefinition> Options { get; set; } = [];
}

public sealed class PluginOptionDefinition
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string DefaultValue { get; set; } = "";
}

public sealed record PluginOptionValue(string Key, string Label, string Value);

public sealed class PluginEntry
{
    public string Id { get; internal set; } = "";
    public string Name { get; internal set; } = "";
    public string Description { get; internal set; } = "";
    public string Kind { get; internal set; } = "";
    public string Version { get; internal set; } = "";
    public bool IsEnabled { get; internal set; }
    public string Status { get; internal set; } = "";
    public IReadOnlyList<PluginOptionValue> Options { get; internal set; } = [];
}
