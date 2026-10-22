using PowerHub.Models;

namespace PowerHub.Services;

public sealed record AutomationSnapshot(DateTimeOffset At, IReadOnlyList<DeviceStatus> Devices, IReadOnlySet<string> Processes,
    bool DevicesAvailable = true, bool ProcessesAvailable = true);
public sealed record AutomationDecision(AutomationRule Rule, bool Restore, string Reason);
public sealed record AutomationConditionResult(bool Passed, string Description);
public sealed record AutomationRulePreview(bool Passed, IReadOnlyList<AutomationConditionResult> Conditions, IReadOnlyList<string> Errors);

public sealed record AutomationAudioOutput(string Id, string Name);
public sealed record AutomationAudioState(IReadOnlyList<AutomationAudioOutput> Outputs, string? DefaultOutputId,
    double? Volume, bool? OutputMuted, string? MicrophoneId, bool? MicrophoneMuted, string? Error);
public sealed record AutomationAudioRestoreResult(IReadOnlyList<string> Messages, bool RetryRequired = false);

public interface IAutomationAudio : IDisposable
{
    AutomationAudioState Read();
    object Capture();
    void SetExecutionScope(string? ruleId) { }
    void SetOutput(string id);
    void SetVolume(double value);
    void SetOutputMuted(bool muted);
    void SetMicrophoneMuted(bool muted);
    AutomationAudioRestoreResult Restore(object captured, object expected, IReadOnlySet<AutomationActionKind> changed);
}
