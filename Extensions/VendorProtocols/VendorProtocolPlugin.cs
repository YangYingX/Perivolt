using PowerHub.Models;
using PowerHub.Native;
using PowerHub.Plugins;
using PowerHub.Services;

namespace PowerHub.Extensions;

public sealed class VendorProtocolPlugin : IPowerHubPlugin
{
    public HidPluginResult? ReadHid(HidDeviceContext request)
    {
        var caps = new HidNative.Caps { UsagePage=request.UsagePage, Usage=request.Usage,
            InputReportByteLength=request.InputReportByteLength, OutputReportByteLength=request.OutputReportByteLength,
            FeatureReportByteLength=request.FeatureReportByteLength };
        var path=request.Path;var device=request.Device;
        var children=LogitechBatteryReader.TryRead(path,device,caps,out var detail);
        if(children is not null)
            return new(device with { ConnectionKnown=false,IsConnected=false,HasProtocolConnectionState=true,
                Source="Logitech HID++ 接收器", Message="接收器本次未返回在线外设电量" },detail,children);
        var reading=WeishengBatteryReader.TryRead(path,device,caps,out detail);
        reading ??= RazerBatteryReader.TryRead(path,device,caps,out detail);
        reading ??= CompxBatteryReader.TryRead(path,device,caps,out detail);
        reading ??= AulaBatteryReader.TryRead(path,device,caps,out detail);
        reading ??= DualSenseBatteryReader.TryRead(path,device,caps,out detail);
        reading ??= SteamControllerBatteryReader.TryRead(path,device,caps,out detail);
        return reading is null ? null : new(reading,detail);
    }
}
