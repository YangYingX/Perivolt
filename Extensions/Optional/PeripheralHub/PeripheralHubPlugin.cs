using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PowerHub.Models;
using PowerHub.Plugins;

namespace Perivolt.Extensions.PeripheralHub;

public sealed class PeripheralHubPlugin : IInteractivePowerHubPlugin
{
    private HubHotkey? _hotkey;
    private HubWindow? _window;
    private AudioController? _audio;
    private DispatcherTimer? _audioTimer, _volumeTimer;
    private Dispatcher? _dispatcher;
    private string? _shortcut;
    private string? _displayedOutputId, _displayedMicrophoneId, _pendingOutputId;
    private double? _pendingVolume;
    private bool _initialized, _disposed;

    internal IReadOnlyList<DeviceStatus> Devices { get; private set; } = [];
    internal DateTimeOffset LastScanAt { get; private set; }
    internal string HotkeyStatus { get; private set; } = "正在注册快捷键…";
    internal string Status { get; private set; } = "";

    public void Initialize(PluginUiContext context)
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _audioTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _audioTimer.Tick += OnAudioTick;
        _volumeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _volumeTimer.Tick += OnVolumeTick;
        try { _hotkey = new HubHotkey(Toggle); }
        catch (Exception exception) { HotkeyStatus = "快捷键不可用：" + exception.Message + "；可从插件页打开"; }
        // Options are applied on the first snapshot, before registering any default shortcut.
    }

    public void Update(PluginUiSnapshot snapshot)
    {
        if (!_initialized || _disposed) return;
        _dispatcher!.VerifyAccess();
        var shortcut = snapshot.Options.TryGetValue("hotkey", out var configured) ? configured.Trim() : "Ctrl+Alt+P";
        if (_shortcut != shortcut)
        {
            _shortcut = shortcut;
            if (_hotkey is not null) HotkeyStatus = _hotkey.Configure(shortcut);
            _window?.SetHotkeyStatus(HotkeyStatus);
        }
        Devices = snapshot.Devices.ToArray();
        LastScanAt = snapshot.ScannedAt;
        _window?.RefreshDevices();
    }

    public void Open()
    {
        if (_disposed || !_initialized) return;
        _dispatcher!.VerifyAccess();
        try
        {
            if (_window is null)
            {
                var window = new HubWindow(this);
                _window = window;
                window.VolumeChanged += QueueVolume;
                window.OutputMuteRequested += muted =>
                {
                    if (_displayedOutputId is { } id) RunAudio(audio => audio.SetOutputMuted(muted, id));
                };
                window.MicrophoneMuteRequested += muted =>
                {
                    if (_displayedMicrophoneId is { } id) RunAudio(audio => audio.SetMicrophoneMuted(muted, id));
                };
                window.OutputSelected += id =>
                {
                    CancelPendingVolume();
                    RunAudio(audio => audio.SetDefaultOutput(id));
                };
                window.SoundSettingsRequested += OpenSoundSettings;
                window.AudioInteractionEnded += RefreshAudio;
                window.Closed += OnWindowClosed;
                window.IsVisibleChanged += OnVisibilityChanged;
                window.StateChanged += OnWindowStateChanged;
                window.Show();
            }
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            RefreshAudio();
            UpdatePolling();
        }
        catch (Exception exception)
        {
            Status = "无法打开快捷中枢：" + exception.Message;
            _window?.SetStatus(Status);
        }
    }

    private void Toggle()
    {
        if (_disposed) return;
        if (_window is { IsActive: true }) _window.Close();
        else Open();
    }

    private bool CanPoll => !_disposed && _window is { IsVisible: true, WindowState: not WindowState.Minimized };
    private void UpdatePolling()
    {
        if (CanPoll) _audioTimer?.Start();
        else { _audioTimer?.Stop(); CancelPendingVolume(); }
    }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdatePolling();
    private void OnWindowStateChanged(object? sender, EventArgs args)
    {
        UpdatePolling();
        if (CanPoll) RefreshAudio();
    }
    private void OnAudioTick(object? sender, EventArgs args)
    {
        if (CanPoll && _pendingVolume is null) RefreshAudio();
    }
    private void RefreshAudio()
    {
        if (!CanPoll || _window!.IsEditingAudio || _pendingVolume is not null) return;
        try
        {
            var snapshot = (_audio ??= new AudioController()).Read();
            _displayedOutputId = snapshot.DefaultOutputId;
            _displayedMicrophoneId = snapshot.MicrophoneId;
            _window!.RefreshAudio(snapshot);
        }
        catch (Exception exception)
        {
            _displayedOutputId = null;
            _displayedMicrophoneId = null;
            _window!.RefreshAudio(new([], null, null, null, null, null, "无法读取系统音频：" + exception.Message, "无法读取麦克风"));
        }
    }
    private void QueueVolume(double value)
    {
        if (!CanPoll || !double.IsFinite(value) || _displayedOutputId is null) return;
        _pendingVolume = Math.Clamp(value, 0, 1);
        _pendingOutputId = _displayedOutputId;
        _volumeTimer!.Stop();
        _volumeTimer.Start();
    }
    private void OnVolumeTick(object? sender, EventArgs args)
    {
        _volumeTimer!.Stop();
        var value = _pendingVolume;
        var outputId = _pendingOutputId;
        _pendingVolume = null;
        _pendingOutputId = null;
        if (CanPoll && value is { } volume && outputId is not null) RunAudio(audio => audio.SetVolume(volume, outputId));
    }
    private void CancelPendingVolume()
    {
        _volumeTimer?.Stop();
        _pendingVolume = null;
        _pendingOutputId = null;
    }
    private void RunAudio(Action<AudioController> action)
    {
        if (!CanPoll) return;
        try
        {
            action(_audio ??= new AudioController());
            Status = "";
        }
        catch (Exception exception) { Status = "操作未完成：" + exception.Message; }
        _window?.SetStatus(Status);
        RefreshAudio();
    }
    private void OpenSoundSettings()
    {
        if (_disposed) return;
        try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
        catch (Exception exception) { Status = "无法打开声音设置：" + exception.Message; _window?.SetStatus(Status); }
    }
    private void OnWindowClosed(object? sender, EventArgs args)
    {
        if (sender is HubWindow window)
        {
            window.Closed -= OnWindowClosed;
            window.IsVisibleChanged -= OnVisibilityChanged;
            window.StateChanged -= OnWindowStateChanged;
        }
        _window = null;
        _audioTimer?.Stop();
        CancelPendingVolume();
        _displayedOutputId = null;
        _displayedMicrophoneId = null;
        _audio?.Dispose();
        _audio = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audioTimer?.Stop();
        _volumeTimer?.Stop();
        if (_audioTimer is not null) _audioTimer.Tick -= OnAudioTick;
        if (_volumeTimer is not null) _volumeTimer.Tick -= OnVolumeTick;
        CancelPendingVolume();
        _hotkey?.Dispose();
        _hotkey = null;
        _window?.Close();
        _audio?.Dispose();
        _audio = null;
        Devices = [];
    }
}
