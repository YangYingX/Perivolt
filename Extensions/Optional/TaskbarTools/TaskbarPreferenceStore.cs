using System.IO;
using System.Text;
using System.Text.Json;

namespace Perivolt.TaskbarTools;

internal sealed record TaskbarPreferences
{
    public bool AllDisplays { get; init; } = true;
    public Dictionary<string, int> MonitorOffsets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class TaskbarPreferenceStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly CancellationTokenSource _loadCancellation = new();
    private Action<string?>? _reportError;
    private FileStream? _lease;
    private TaskbarPreferences? _pending;
    private Task _tail;
    private bool _writing;
    private bool _loaded;
    private bool _closing;
    public Task<TaskbarPreferences> Loading { get; }

    public TaskbarPreferenceStore(string directory, Action<string?> reportError)
    {
        _directory = directory;
        _reportError = reportError;
        Loading = Task.Run(LoadAsync);
        _tail = Loading;
    }

    private async Task<TaskbarPreferences> LoadAsync()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            // Keep one lifetime lease until this instance's final pending write has completed.
            // A newly enabled instance reads only after the old instance has released its lease.
            for (var attempt = 0; ; attempt++)
            {
                _loadCancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    _lease = new FileStream(Path.Combine(_directory, "taskbar-tools.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException) when (attempt < 39) { await Task.Delay(50, _loadCancellation.Token).ConfigureAwait(false); }
            }
            _loadCancellation.Token.ThrowIfCancellationRequested();
            var path = Path.Combine(_directory, "taskbar-tools.json");
            var preferences = new TaskbarPreferences();
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Settings exceed the size limit");
                var text = await File.ReadAllTextAsync(path, Encoding.UTF8, _loadCancellation.Token).ConfigureAwait(false);
                preferences = JsonSerializer.Deserialize<TaskbarPreferences>(text) ?? throw new InvalidDataException("Invalid taskbar settings");
            }
            var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in preferences.MonitorOffsets ?? [])
            {
                if (offsets.Count == 32) break;
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 128) offsets[pair.Key] = Math.Clamp(pair.Value, 0, 32767);
            }
            lock (_gate) _loaded = true;
            return preferences with { MonitorOffsets = offsets };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Report("无法读取多屏设置，请检查插件数据目录。多屏显示暂未启用。");
            throw;
        }
    }

    public void Save(TaskbarPreferences preferences)
    {
        lock (_gate)
        {
            if (_closing || !_loaded) return;
            _pending = preferences;
            if (_writing) return;
            _writing = true;
            var previous = _tail;
            _tail = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }
                await WritePendingAsync().ConfigureAwait(false);
            });
        }
    }

    private async Task WritePendingAsync()
    {
        try
        {
            while (true)
            {
                TaskbarPreferences? preferences;
                lock (_gate)
                {
                    preferences = _pending;
                    _pending = null;
                    if (preferences is null) { _writing = false; return; }
                }
                var temporary = Path.Combine(_directory, "taskbar-tools.json.tmp");
                try
                {
                    var text = JsonSerializer.Serialize(preferences);
                    await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false)).ConfigureAwait(false);
                    File.Move(temporary, Path.Combine(_directory, "taskbar-tools.json"), true);
                    Report(null);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report("多屏设置未能保存，请检查插件数据目录权限。");
            lock (_gate) _writing = false;
            throw;
        }
    }

    private void Report(string? message)
    {
        Action<string?>? callback;
        lock (_gate) callback = _reportError;
        try { callback?.Invoke(message); } catch (Exception) { }
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return _tail.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            _reportError = null;
            if (!_loaded) _loadCancellation.Cancel();
            var previous = _tail;
            _tail = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }
                finally { _lease?.Dispose(); _loadCancellation.Dispose(); }
            });
        }
    }
}
