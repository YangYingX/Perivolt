using System.Runtime.InteropServices;
using System.Collections.Frozen;
using static Perivolt.Extensions.PeripheralHub.AudioInterop;

namespace Perivolt.Extensions.PeripheralHub;

internal sealed record AudioEndpoint(string Id, string Name);
internal sealed record AudioSnapshot(IReadOnlyList<AudioEndpoint> Outputs, string? DefaultOutputId, double? Volume,
    bool? OutputMuted, string? MicrophoneName, bool? MicrophoneMuted, string? OutputError = null, string? MicrophoneError = null)
{
    public string? MicrophoneId { get; init; }
}

internal sealed record AudioDeviceState(string Id, string Name, double? Volume, bool? Muted);
internal sealed record AudioRestoreSnapshot(IReadOnlyDictionary<Role, string> DefaultOutputs,
    IReadOnlyDictionary<string, AudioDeviceState> Outputs, IReadOnlyDictionary<string, AudioDeviceState> Microphones,
    string? DefaultMicrophoneId, IReadOnlyList<string> Errors);

internal sealed class AudioController : IDisposable
{
    private static readonly Role[] OutputRoles = [Role.Console, Role.Multimedia, Role.Communications];
    private bool _disposed;

    public AudioSnapshot Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IDeviceEnumerator? enumerator = null;
        IReadOnlyList<AudioEndpoint> outputs = [];
        string? outputId = null, microphoneId = null, microphoneName = null, outputError = null, microphoneError = null;
        double? volume = null;
        bool? outputMuted = null, microphoneMuted = null;
        try
        {
            enumerator = CreateEnumerator();
            try { outputs = EnumerateOutputs(enumerator); }
            catch (Exception exception) { outputError = Describe("无法读取音频输出列表", exception); }
            IDevice? output = null;
            IEndpointVolume? endpoint = null;
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out output));
                Check(output.GetId(out outputId));
                endpoint = ActivateVolume(output);
                Check(endpoint.GetMasterVolumeLevelScalar(out var scalar));
                if (!float.IsFinite(scalar) || scalar is < 0 or > 1) throw new InvalidOperationException("Windows 返回了无效音量。");
                volume = scalar;
                Check(endpoint.GetMute(out var muted));
                outputMuted = muted;
            }
            catch (Exception exception) { outputError = Append(outputError, Describe("无法读取默认输出", exception)); }
            finally { Release(endpoint); Release(output); }

            IDevice? microphone = null;
            endpoint = null;
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out microphone));
                Check(microphone.GetId(out microphoneId));
                microphoneName = ReadName(microphone, "默认通信麦克风");
                endpoint = ActivateVolume(microphone);
                Check(endpoint.GetMute(out var muted));
                microphoneMuted = muted;
            }
            catch (Exception exception) { microphoneError = Describe("无法读取默认通信麦克风", exception); }
            finally { Release(endpoint); Release(microphone); }
        }
        catch (Exception exception)
        {
            outputError = Describe("Windows 音频服务不可用", exception);
            microphoneError = outputError;
        }
        finally { Release(enumerator); }
        return new(outputs, outputId, volume, outputMuted, microphoneName, microphoneMuted, outputError, microphoneError) { MicrophoneId = microphoneId };
    }

    public void SetVolume(double value, string? expectedOutputId = null) => SetVolumeCore(value, expectedOutputId, null);
    internal void SetVolumeForAutomation(double value, string expectedOutputId, Action<double> beforeWrite) => SetVolumeCore(value, expectedOutputId, beforeWrite);

    private void SetVolumeCore(double value, string? expectedOutputId, Action<double>? beforeWrite)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
        WithDefaultVolume(DataFlow.Render, Role.Multimedia, endpoint =>
        {
            if (beforeWrite is not null)
            {
                Check(endpoint.GetMasterVolumeLevelScalar(out var original));
                if (!float.IsFinite(original) || original is < 0 or > 1) throw new InvalidOperationException("原输出音量不可用，未执行修改。");
                beforeWrite(original);
            }
            var context = Guid.Empty;
            Check(endpoint.SetMasterVolumeLevelScalar((float)value, ref context));
            Check(endpoint.GetMasterVolumeLevelScalar(out var actual));
            if (!float.IsFinite(actual) || Math.Abs(actual - value) > .002)
                throw new InvalidOperationException("Windows 未确认输出音量已改变，请刷新后重试。");
        }, expectedOutputId);
    }

    public void SetOutputMuted(bool muted, string? expectedOutputId = null) => SetMuted(DataFlow.Render, Role.Multimedia, muted, expectedOutputId);
    public void SetMicrophoneMuted(bool muted, string? expectedMicrophoneId = null) => SetMuted(DataFlow.Capture, Role.Communications, muted, expectedMicrophoneId);
    internal void SetOutputMutedForAutomation(bool muted, string expectedOutputId, Action<bool> beforeWrite) => SetMuted(DataFlow.Render, Role.Multimedia, muted, expectedOutputId, beforeWrite);
    internal void SetMicrophoneMutedForAutomation(bool muted, string expectedMicrophoneId, Action<bool> beforeWrite) => SetMuted(DataFlow.Capture, Role.Communications, muted, expectedMicrophoneId, beforeWrite);

    public void SetDefaultOutput(string id) => SetDefaultOutputCore(id, null);
    internal void SetDefaultOutputForAutomation(string id, Action<Role, string> beforeWrite) => SetDefaultOutputCore(id, beforeWrite);

    private void SetDefaultOutputCore(string id, Action<Role, string>? beforeWrite)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        IDeviceEnumerator? enumerator = null;
        IPolicyConfig? policy = null;
        try
        {
            enumerator = CreateEnumerator();
            if (!EnumerateOutputs(enumerator).Any(endpoint => string.Equals(endpoint.Id, id, StringComparison.Ordinal)))
                throw new InvalidOperationException("所选音频输出已断开或不可用，请刷新后重试。");
            var previous = OutputRoles.ToDictionary(role => role, role => DefaultOutputId(enumerator, role));
            policy = CreateCom<IPolicyConfig>(PolicyClass);
            var attempted = new List<Role>();
            try
            {
                foreach (var role in OutputRoles)
                {
                    if (string.Equals(previous[role], id, StringComparison.Ordinal)) continue;
                    if (!string.Equals(DefaultOutputId(enumerator, role), previous[role], StringComparison.Ordinal))
                        throw new InvalidOperationException("默认输出已变化，未继续切换。");
                    beforeWrite?.Invoke(role, previous[role]);
                    attempted.Add(role);
                    Check(policy.SetDefaultEndpoint(id, role));
                }
                foreach (var role in OutputRoles)
                    if (!string.Equals(DefaultOutputId(enumerator, role), id, StringComparison.Ordinal))
                        throw new InvalidOperationException("Windows 未确认所有默认输出角色已切换。");
            }
            catch (Exception exception)
            {
                var unrestored = new List<string>();
                foreach (var role in attempted.AsEnumerable().Reverse())
                {
                    try
                    {
                        var actual = DefaultOutputId(enumerator, role);
                        if (string.Equals(actual, previous[role], StringComparison.Ordinal)) continue;
                        if (!string.Equals(actual, id, StringComparison.Ordinal))
                        { unrestored.Add(RoleName(role)); continue; }
                        Check(policy.SetDefaultEndpoint(previous[role], role));
                        if (!string.Equals(DefaultOutputId(enumerator, role), previous[role], StringComparison.Ordinal))
                            unrestored.Add(RoleName(role));
                    }
                    catch { unrestored.Add(RoleName(role)); }
                }
                var status = unrestored.Count == 0 ? "已恢复先前的默认输出。" : "以下角色未能恢复：" + string.Join("、", unrestored) + "。";
                throw new InvalidOperationException("默认输出切换失败；" + status + "请通过 Windows 声音设置选择输出。", exception);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException)
        {
            throw new InvalidOperationException("此系统无法使用快捷输出切换，请通过 Windows 声音设置选择输出。", exception);
        }
        finally { Release(policy); Release(enumerator); }
    }

    public void Dispose() => _disposed = true;

    // Snapshots contain values only. No COM objects or audio streams outlive these calls.
    internal AudioRestoreSnapshot CaptureRestoreSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            var errors = new List<string>();
            var defaults = new Dictionary<Role, string>();
            foreach (var role in OutputRoles)
            {
                try { defaults[role] = DefaultOutputId(enumerator, role); }
                catch (Exception error) { errors.Add(Describe("无法读取默认输出", error)); }
            }
            string? microphoneId = null;
            IDevice? microphone = null;
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out microphone));
                Check(microphone.GetId(out microphoneId));
            }
            catch (Exception error) { errors.Add(Describe("无法读取默认通信麦克风", error)); }
            finally { Release(microphone); }
            var outputs = CaptureEndpoints(enumerator, DataFlow.Render, errors);
            var microphones = CaptureEndpoints(enumerator, DataFlow.Capture, errors);
            return new(defaults.ToFrozenDictionary(), outputs, microphones, microphoneId, errors.ToArray());
        }
        finally { Release(enumerator); }
    }

    internal bool RestoreOutputVolume(string id, double expected, double original)
    {
        var restored = false;
        WithEndpoint(id, endpoint =>
        {
            Check(endpoint.GetMasterVolumeLevelScalar(out var current));
            if (!float.IsFinite(current)) throw new InvalidOperationException("无法读取当前音频状态，请稍后重试恢复。");
            if (Math.Abs(current - original) <= .002) { restored = true; return; }
            if (Math.Abs(current - expected) > .002) return;
            var context = Guid.Empty;
            Check(endpoint.SetMasterVolumeLevelScalar((float)original, ref context));
            Check(endpoint.GetMasterVolumeLevelScalar(out var actual));
            if (!float.IsFinite(actual) || Math.Abs(actual - original) > .002)
                throw new InvalidOperationException("Windows 未确认输出音量已恢复。");
            restored = true;
        });
        return restored;
    }

    internal bool RestoreEndpointMuted(string id, bool expected, bool original)
    {
        var restored = false;
        WithEndpoint(id, endpoint =>
        {
            Check(endpoint.GetMute(out var current));
            if (current == original) { restored = true; return; }
            if (current != expected) return;
            var context = Guid.Empty;
            Check(endpoint.SetMute(original, ref context));
            Check(endpoint.GetMute(out var actual));
            if (actual != original) throw new InvalidOperationException("Windows 未确认静音状态已恢复。");
            restored = true;
        });
        return restored;
    }

    internal bool RestoreDefaultOutput(Role role, string expected, string original)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IDeviceEnumerator? enumerator = null;
        IDevice? target = null;
        IPolicyConfig? policy = null;
        try
        {
            enumerator = CreateEnumerator();
            var current = DefaultOutputId(enumerator, role);
            if (string.Equals(current, original, StringComparison.Ordinal)) return true;
            if (!string.Equals(current, expected, StringComparison.Ordinal)) return false;
            Check(enumerator.GetDevice(original, out target));
            Check(target.GetState(out var state));
            if ((state & DeviceStateActive) == 0) throw new InvalidOperationException("原音频输出已断开，无法恢复。");
            policy = CreateCom<IPolicyConfig>(PolicyClass);
            // Recheck after creating the COM setter so an intervening user switch is retained.
            current = DefaultOutputId(enumerator, role);
            if (string.Equals(current, original, StringComparison.Ordinal)) return true;
            if (!string.Equals(current, expected, StringComparison.Ordinal)) return false;
            Check(policy.SetDefaultEndpoint(original, role));
            if (!string.Equals(DefaultOutputId(enumerator, role), original, StringComparison.Ordinal))
                throw new InvalidOperationException("Windows 未确认默认输出已恢复。");
            return true;
        }
        finally { Release(policy); Release(target); Release(enumerator); }
    }

    private static IReadOnlyDictionary<string, AudioDeviceState> CaptureEndpoints(IDeviceEnumerator enumerator, DataFlow flow, List<string> errors)
    {
        IDeviceCollection? collection = null;
        var states = new Dictionary<string, AudioDeviceState>(StringComparer.Ordinal);
        try
        {
            Check(enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out collection));
            Check(collection.GetCount(out var count));
            for (uint index = 0; index < count; index++)
            {
                IDevice? device = null;
                IEndpointVolume? endpoint = null;
                try
                {
                    Check(collection.Item(index, out device));
                    Check(device.GetState(out var state));
                    if ((state & DeviceStateActive) == 0) continue;
                    Check(device.GetId(out var id));
                    var name = ReadName(device, flow == DataFlow.Render ? "音频输出" : "麦克风");
                    double? volume = null;
                    bool? muted = null;
                    try
                    {
                        endpoint = ActivateVolume(device);
                        if (flow == DataFlow.Render)
                        {
                            Check(endpoint.GetMasterVolumeLevelScalar(out var scalar));
                            if (float.IsFinite(scalar) && scalar is >= 0 and <= 1) volume = scalar;
                        }
                        Check(endpoint.GetMute(out var value));
                        muted = value;
                    }
                    catch (Exception error) { errors.Add(Describe("无法读取音频端点状态", error)); }
                    states[id] = new(id, name, volume, muted);
                }
                catch (Exception error) { errors.Add(Describe("无法读取音频端点", error)); }
                finally { Release(endpoint); Release(device); }
            }
        }
        catch (Exception error) { errors.Add(Describe("无法读取音频设备列表", error)); }
        finally { Release(collection); }
        return states.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private void WithEndpoint(string id, Action<IEndpointVolume> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IDeviceEnumerator? enumerator = null;
        IDevice? device = null;
        IEndpointVolume? endpoint = null;
        try
        {
            enumerator = CreateEnumerator();
            Check(enumerator.GetDevice(id, out device));
            Check(device.GetState(out var state));
            if ((state & DeviceStateActive) == 0) throw new InvalidOperationException("音频端点已断开，无法恢复。");
            endpoint = ActivateVolume(device);
            action(endpoint);
        }
        finally { Release(endpoint); Release(device); Release(enumerator); }
    }

    private void SetMuted(DataFlow flow, Role role, bool muted, string? expectedOutputId = null, Action<bool>? beforeWrite = null) => WithDefaultVolume(flow, role, endpoint =>
    {
        if (beforeWrite is not null)
        {
            Check(endpoint.GetMute(out var original));
            beforeWrite(original);
        }
        var context = Guid.Empty;
        Check(endpoint.SetMute(muted, ref context));
        Check(endpoint.GetMute(out var actual));
        if (actual != muted) throw new InvalidOperationException("Windows 未确认静音状态已改变，请刷新后重试。");
    }, expectedOutputId);

    private void WithDefaultVolume(DataFlow flow, Role role, Action<IEndpointVolume> action, string? expectedOutputId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IDeviceEnumerator? enumerator = null;
        IDevice? device = null;
        IEndpointVolume? endpoint = null;
        try
        {
            enumerator = CreateEnumerator();
            Check(enumerator.GetDefaultAudioEndpoint(flow, role, out device));
            if (expectedOutputId is not null)
            {
                Check(device.GetId(out var actualId));
                if (!string.Equals(actualId, expectedOutputId, StringComparison.Ordinal))
                    throw new InvalidOperationException(flow == DataFlow.Capture ? "默认通信麦克风已变化，请重试。" : "默认输出已变化，请重试。");
            }
            endpoint = ActivateVolume(device);
            action(endpoint);
        }
        finally { Release(endpoint); Release(device); Release(enumerator); }
    }

    private static IReadOnlyList<AudioEndpoint> EnumerateOutputs(IDeviceEnumerator enumerator)
    {
        IDeviceCollection? collection = null;
        try
        {
            Check(enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceStateActive, out collection));
            Check(collection.GetCount(out var count));
            var outputs = new List<AudioEndpoint>();
            for (uint index = 0; index < count; index++)
            {
                IDevice? device = null;
                try
                {
                    Check(collection.Item(index, out device));
                    Check(device.GetState(out var state));
                    if ((state & DeviceStateActive) == 0) continue;
                    Check(device.GetId(out var id));
                    outputs.Add(new(id, ReadName(device, $"音频输出 {index + 1}")));
                }
                catch (COMException) { /* A device can disappear between enumeration and reading its properties. */ }
                finally { Release(device); }
            }
            return outputs.OrderBy(output => output.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { Release(collection); }
    }

    private static string ReadName(IDevice device, string fallback)
    {
        IPropertyStore? store = null;
        var value = new PropVariant();
        try
        {
            Check(device.OpenPropertyStore(0, out store));
            var key = FriendlyName;
            Check(store.GetValue(ref key, out value));
            return value.Type == 31 && value.String != IntPtr.Zero && Marshal.PtrToStringUni(value.String) is { Length: > 0 } name ? name : fallback;
        }
        catch (COMException) { return fallback; }
        finally { PropVariantClear(ref value); Release(store); }
    }

    private static string DefaultOutputId(IDeviceEnumerator enumerator, Role role)
    {
        IDevice? device = null;
        try
        {
            Check(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role, out device));
            Check(device.GetId(out var id));
            return id;
        }
        finally { Release(device); }
    }

    private static IDeviceEnumerator CreateEnumerator() => CreateCom<IDeviceEnumerator>(EnumeratorClass);
    private static T CreateCom<T>(Guid id)
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(id, throwOnError: true)!) ?? throw new COMException("Windows 音频组件未创建。");
        try { return (T)instance; }
        catch { Release(instance); throw; }
    }
    private static IEndpointVolume ActivateVolume(IDevice device)
    {
        var iid = EndpointVolumeInterface;
        Check(device.Activate(ref iid, ClassContextAll, IntPtr.Zero, out var instance));
        try { return (IEndpointVolume)instance; }
        catch { Release(instance); throw; }
    }
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
    }
    private static string RoleName(Role role) => role switch { Role.Console => "系统", Role.Multimedia => "多媒体", _ => "通信" };
    private static string Append(string? previous, string next) => previous is null ? next : previous + "；" + next;
    private static string Describe(string action, Exception exception) => exception.HResult == unchecked((int)0x80070490)
        ? action + "：未找到可用设备。" : action + $"：{exception.Message}（0x{exception.HResult:X8}）";
}
