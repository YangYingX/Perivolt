using Perivolt.Extensions.PeripheralHub;
using PowerHub.Models;
using static Perivolt.Extensions.PeripheralHub.AudioInterop;

namespace PowerHub.Services;

public sealed class AutomationAudio : IAutomationAudio
{
    private readonly AudioController controller = new();
    private readonly Guid owner = Guid.NewGuid();
    private readonly List<WeakReference<AudioCapture>> captures = [];
    private long sequence;
    private string? executionScope;

    public AutomationAudioState Read()
    {
        var state = controller.Read();
        var errors = new[] { state.OutputError, state.MicrophoneError }.Where(message => !string.IsNullOrWhiteSpace(message));
        return new(state.Outputs.Select(output => new AutomationAudioOutput(output.Id, output.Name)).ToArray(),
            state.DefaultOutputId, state.Volume, state.OutputMuted, state.MicrophoneId, state.MicrophoneMuted,
            string.Join("；", errors) is { Length: > 0 } error ? error : null);
    }

    public object Capture()
    {
        var capture = new AudioCapture(owner, controller.CaptureRestoreSnapshot(), sequence, executionScope);
        captures.RemoveAll(reference => !reference.TryGetTarget(out _));
        captures.Add(new(capture));
        return capture;
    }
    public void SetExecutionScope(string? ruleId) => executionScope = ruleId;
    public void SetOutput(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        controller.SetDefaultOutputForAutomation(id, (role, original) =>
            Record(new(0, AutomationActionKind.SetAudioOutput, id, Role: role, OriginalOutputId: original)));
    }
    public void SetVolume(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
        var output = RequireOutput(controller.CaptureRestoreSnapshot());
        controller.SetVolumeForAutomation(value, output.Id, original => Record(new(0, AutomationActionKind.SetVolume, output.Id, OriginalVolume: original)));
    }
    public void SetOutputMuted(bool muted)
    {
        var output = RequireOutput(controller.CaptureRestoreSnapshot());
        controller.SetOutputMutedForAutomation(muted, output.Id, original => Record(new(0, AutomationActionKind.SetOutputMuted, output.Id, OriginalMuted: original)));
    }
    public void SetMicrophoneMuted(bool muted)
    {
        var state = controller.CaptureRestoreSnapshot();
        var id = state.DefaultMicrophoneId;
        if (id is null)
            throw new InvalidOperationException(L.T("默认通信麦克风不可用。"));
        controller.SetMicrophoneMutedForAutomation(muted, id, original => Record(new(0, AutomationActionKind.SetMicrophoneMuted, id, OriginalMuted: original)));
    }

    private static AudioDeviceState RequireOutput(AudioRestoreSnapshot state) =>
        state.DefaultOutputs.TryGetValue(Role.Multimedia, out var id) && state.Outputs.TryGetValue(id, out var output)
            ? output : throw new InvalidOperationException(L.T("默认音频输出不可用。"));

    private void Record(AudioMutation mutation)
    {
        var stamped = mutation with { Sequence = ++sequence };
        captures.RemoveAll(reference =>
        {
            if (!reference.TryGetTarget(out var capture)) return true;
            if (string.Equals(capture.Scope, executionScope, StringComparison.OrdinalIgnoreCase)) capture.Track(stamped);
            return false;
        });
    }

    public AutomationAudioRestoreResult Restore(object captured, object expected, IReadOnlySet<AutomationActionKind> changed)
    {
        if (captured is not AudioCapture before || expected is not AudioCapture after || before.Owner != owner || after.Owner != owner || !string.Equals(after.Scope, before.Scope, StringComparison.OrdinalIgnoreCase) || after.Sequence < before.Sequence)
            throw new ArgumentException(L.T("音频恢复快照无效。"));
        AudioRestoreSnapshot current;
        try { current = controller.CaptureRestoreSnapshot(); }
        catch (Exception error) { return new([L.F("部分音频设置未能恢复：{0}", error.Message)], true); }
        var plan = PlanRestoration(before, after, current, changed);
        var notes = plan.Notes.ToList();
        var retry = plan.RetryRequired;
        foreach (var operation in plan.Operations)
        {
            try
            {
                var restored = operation.Kind switch
                {
                    RestoreKind.Volume => controller.RestoreOutputVolume(operation.Id, operation.ExpectedVolume, operation.OriginalVolume),
                    RestoreKind.OutputMuted or RestoreKind.MicrophoneMuted => controller.RestoreEndpointMuted(operation.Id, operation.ExpectedMuted, operation.OriginalMuted),
                    _ => controller.RestoreDefaultOutput(operation.Role, operation.ExpectedId!, operation.Id)
                };
                if (!restored) notes.Add(L.T("音频状态已被其他操作更改，保留当前值。"));
            }
            catch (Exception error) { notes.Add(L.F("部分音频设置未能恢复：{0}", error.Message)); retry = true; }
        }
        return new(notes, retry);
    }

    // Keep the decision step pure so ownership checks can be verified without changing system audio.
    internal static RestorePlan PlanRestoration(AudioCapture before, AudioCapture expected,
        AudioRestoreSnapshot current, IReadOnlySet<AutomationActionKind> changed)
    {
        var operations = new List<RestoreOperation>();
        var notes = new List<string>();
        var retry = false;
        var owned = before.Mutations.Where(mutation => mutation.Sequence > before.Sequence && mutation.Sequence <= expected.Sequence && changed.Contains(mutation.Kind))
            .OrderBy(mutation => mutation.Sequence)
            .GroupBy(mutation => (mutation.Kind, Key: mutation.Kind == AutomationActionKind.SetAudioOutput ? mutation.Role?.ToString() ?? "unknown" : mutation.Id));
        foreach (var group in owned.OrderBy(group => group.Key.Kind == AutomationActionKind.SetAudioOutput ? 1 : 0))
        {
            var original = group.First();
            if (original.Kind == AutomationActionKind.SetAudioOutput)
            {
                if (original.Role is not Role role || original.OriginalOutputId is not { } originalId)
                { notes.Add(L.T("缺少原默认输出快照，未更改该输出角色。")); retry = true; continue; }
                if (!current.DefaultOutputs.TryGetValue(role, out var currentId))
                { notes.Add(L.T("无法读取当前音频状态，请稍后重试恢复。")); retry = true; continue; }
                if (currentId == originalId) continue;
                if (!expected.State.DefaultOutputs.TryGetValue(role, out var expectedId))
                { notes.Add(L.T("缺少原默认输出快照，未更改该输出角色。")); retry = true; continue; }
                if (originalId == expectedId) continue;
                if (currentId != expectedId)
                { notes.Add(L.T("默认音频输出已被其他操作更改，保留当前选择。")); continue; }
                if (!current.Outputs.ContainsKey(originalId))
                { notes.Add(L.T("原音频输出已断开，暂时无法恢复。")); retry = true; continue; }
                operations.Add(new(RestoreKind.DefaultOutput, originalId, Role: role, ExpectedId: expectedId));
                continue;
            }
            var microphone = original.Kind == AutomationActionKind.SetMicrophoneMuted;
            var expectedStates = microphone ? expected.State.Microphones : expected.State.Outputs;
            var currentStates = microphone ? current.Microphones : current.Outputs;
            var id = original.Id;
            if (!currentStates.TryGetValue(id, out var present))
            { notes.Add(L.T("音频端点已断开，暂时无法恢复。")); retry = true; continue; }
            if (original.Kind == AutomationActionKind.SetVolume)
            {
                if (original.OriginalVolume is not double oldVolume)
                { notes.Add(L.T("音频端点快照不完整，未恢复该设置。")); retry = true; continue; }
                if (present.Volume is not double currentVolume || !double.IsFinite(currentVolume))
                { notes.Add(L.T("无法读取当前音频状态，请稍后重试恢复。")); retry = true; continue; }
                if (Math.Abs(currentVolume - oldVolume) <= .002) continue;
                if (!expectedStates.TryGetValue(id, out var after) || after.Volume is not double expectedVolume)
                { notes.Add(L.T("音频端点快照不完整，未恢复该设置。")); retry = true; continue; }
                if (Math.Abs(oldVolume - expectedVolume) <= .002) continue;
                if (Math.Abs(currentVolume - expectedVolume) <= .002)
                    operations.Add(new(RestoreKind.Volume, id, OriginalVolume: oldVolume, ExpectedVolume: expectedVolume));
                else notes.Add(L.T("输出音量已被其他操作更改，保留当前值。"));
            }
            else
            {
                if (original.OriginalMuted is not bool oldMuted)
                { notes.Add(L.T("音频端点快照不完整，未恢复该设置。")); retry = true; continue; }
                if (present.Muted is not bool currentMuted)
                { notes.Add(L.T("无法读取当前音频状态，请稍后重试恢复。")); retry = true; continue; }
                if (currentMuted == oldMuted) continue;
                if (!expectedStates.TryGetValue(id, out var after) || after.Muted is not bool expectedMuted)
                { notes.Add(L.T("音频端点快照不完整，未恢复该设置。")); retry = true; continue; }
                if (oldMuted == expectedMuted) continue;
                if (currentMuted == expectedMuted)
                    operations.Add(new(microphone ? RestoreKind.MicrophoneMuted : RestoreKind.OutputMuted, id,
                        OriginalMuted: oldMuted, ExpectedMuted: expectedMuted));
                else notes.Add(L.T("静音状态已被其他操作更改，保留当前值。"));
            }
        }
        return new(operations, notes, retry);
    }

    public void Dispose() { controller.Dispose(); captures.Clear(); }

    internal sealed class AudioCapture(Guid owner, AudioRestoreSnapshot state, long sequence, string? scope = null)
    {
        private readonly Dictionary<(AutomationActionKind Kind, string Target), AudioMutation> firstWrites = [];
        public Guid Owner { get; } = owner;
        public AudioRestoreSnapshot State { get; } = state;
        public long Sequence { get; } = sequence;
        public string? Scope { get; } = scope;
        public IReadOnlyCollection<AudioMutation> Mutations => firstWrites.Values;
        // One first-write value per field and live capture boundary preserves long-lived restore points.
        internal void Track(AudioMutation mutation)
        {
            if (mutation.Sequence <= Sequence) return;
            firstWrites.TryAdd((mutation.Kind, mutation.Kind == AutomationActionKind.SetAudioOutput ? mutation.Role?.ToString() ?? "unknown" : mutation.Id), mutation);
        }
    }
    internal sealed record AudioMutation(long Sequence, AutomationActionKind Kind, string Id,
        double? OriginalVolume = null, bool? OriginalMuted = null, Role? Role = null, string? OriginalOutputId = null);
    internal enum RestoreKind { Volume, OutputMuted, MicrophoneMuted, DefaultOutput }
    internal sealed record RestoreOperation(RestoreKind Kind, string Id, double OriginalVolume = 0, double ExpectedVolume = 0,
        bool OriginalMuted = false, bool ExpectedMuted = false, Role Role = Role.Multimedia, string? ExpectedId = null);
    internal sealed record RestorePlan(IReadOnlyList<RestoreOperation> Operations, IReadOnlyList<string> Notes, bool RetryRequired = false);
}
