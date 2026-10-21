using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using PowerHub.Models;
using PowerHub.Plugins;

namespace Perivolt.Extensions.BatteryHistory;

public sealed class BatteryHistoryPlugin : IInteractivePowerHubPlugin
{
    private sealed class Series(string idHash, string name)
    {
        internal string IdHash { get; } = idHash;
        internal string Name { get; set; } = name;
        internal List<HistorySample> Samples { get; } = [];
        internal DateTimeOffset LastObservedAt { get; set; }
        internal bool PendingBreak { get; set; } = true;
    }
    private sealed record SaveRequest(HistoryDocument Document, int Generation, long Revision, TaskCompletionSource<Exception?>? Completion = null);
    private readonly Dictionary<string, Series> _series = [];
    private readonly Channel<SaveRequest> _saves = Channel.CreateBounded<SaveRequest>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _saveTimer;
    private Task? _worker;
    private HistoryWindow? _window;
    private string _filePath = "";
    private DateTimeOffset _enabledAt;
    private DateTimeOffset _lastScanAt;
    private int _generation;
    private long _revision;
    private bool _initialized, _loading = true, _readFailed, _discardLoaded, _dirty, _disposed;
    private string _storageStatus = "正在读取本地历史…";
    private string _scanStatus = "等待启用后的新读数；不会补写之前的缓存。";

    public void Initialize(PluginUiContext context)
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        _enabledAt = DateTimeOffset.UtcNow;
        _dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            _filePath = Path.Combine(Path.GetFullPath(context.DataDirectory), "history.json");
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(30) };
            _saveTimer.Tick += OnSaveTimer;
            _saveTimer.Start();
            _worker = Task.Run(StorageLoopAsync);
        }
        catch (Exception exception)
        {
            _loading = false;
            _readFailed = true;
            _loaded.TrySetResult();
            SetStorageError("无法打开历史存储", exception);
        }
    }

    public void Update(PluginUiSnapshot snapshot)
    {
        if (!_initialized || _disposed) return;
        try
        {
            var scannedAt = snapshot.ScannedAt.ToUniversalTime();
            if (scannedAt <= _lastScanAt || scannedAt > DateTimeOffset.UtcNow.AddSeconds(5)) return;
            var readingFloor = _lastScanAt > _enabledAt ? _lastScanAt : _enabledAt;
            _lastScanAt = scannedAt;
            var present = new HashSet<string>();
            int added = 0, cached = 0;
            foreach (var device in snapshot.Devices)
            {
                if (string.IsNullOrWhiteSpace(device.Id)) continue;
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(device.Id.ToUpperInvariant())));
                present.Add(hash);
                _series.TryGetValue(hash, out var series);
                if (!device.ConnectionKnown || !device.IsConnected || device.BatteryPercent is not (>= 0 and <= 100) || device.UpdatedAt is null)
                {
                    if (series is not null) series.PendingBreak = true;
                    continue;
                }
                var systemReported = device.Source.Contains("Windows", StringComparison.OrdinalIgnoreCase);
                if (ContainsCache(device.Source) || !systemReported && ContainsCache(device.Message))
                {
                    cached++;
                    if (series is not null) series.PendingBreak = true;
                    continue;
                }
                var at = device.UpdatedAt.Value.ToUniversalTime();
                if (at <= readingFloor || at > scannedAt || scannedAt - at > TimeSpan.FromMinutes(2) ||
                    series is not null && at <= series.LastObservedAt)
                {
                    if (series is not null && (at > scannedAt || scannedAt - at > TimeSpan.FromMinutes(2) ||
                        scannedAt - series.LastObservedAt > HistoryStore.MaxLineGap)) series.PendingBreak = true;
                    continue;
                }
                var name = SafeName(device);
                if (series is null)
                {
                    if (_series.Count >= HistoryStore.MaxDevices)
                    {
                        var oldest = _series.Values.MinBy(s => s.Samples.LastOrDefault()?.At ?? DateTimeOffset.MinValue)!;
                        _series.Remove(oldest.IdHash);
                    }
                    series = new(hash, name);
                    _series.Add(hash, series);
                }
                if (series.Name != name) { series.Name = name; MarkDirty(); }
                series.LastObservedAt = at;
                var previous = series.Samples.LastOrDefault();
                var percent = device.BatteryPercent.Value;
                if (previous is not null && previous.Percent == percent && previous.SystemReported == systemReported &&
                    at - previous.At < TimeSpan.FromMinutes(1) && !series.PendingBreak) continue;
                series.Samples.Add(new(at, percent, series.PendingBreak || previous is null || at - previous.At > HistoryStore.MaxLineGap, systemReported));
                series.PendingBreak = false;
                MarkDirty();
                added++;
            }
            foreach (var series in _series.Values)
                if (!present.Contains(series.IdHash)) series.PendingBreak = true;
            Prune(scannedAt);
            _scanStatus = added > 0 ? $"本次新增 {added} 条样本；同值最多每分钟记录一次，变化时记录。" :
                "等待新的有效读数；离线、未知电量及重复时间戳不会新增样本。";
            if (cached > 0) _scanStatus += $" 已跳过 {cached} 台明确标为缓存的读数。";
            RefreshWindow();
        }
        catch (Exception exception)
        {
            _scanStatus = "本次快照未记录：" + exception.Message;
            RefreshWindow();
        }
    }

    public void Open()
    {
        if (_disposed || !_initialized) return;
        try
        {
            if (_window is null)
            {
                _window = new(this);
                _window.Closed += (_, _) => _window = null;
                _window.Show();
            }
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }
        catch (Exception exception) { SetStorageError("无法打开趋势窗口", exception); }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!_initialized || _disposed) return;
        try
        {
            await _loaded.Task.WaitAsync(cancellationToken);
            if (_readFailed || _disposed) return;
            Prune(DateTimeOffset.UtcNow);
            var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new SaveRequest(Snapshot(), _generation, _revision, completion);
            await _saves.Writer.WriteAsync(request, cancellationToken);
            if (_revision == request.Revision) _dirty = false;
            var error = await completion.Task.WaitAsync(cancellationToken);
            if (error is not null) SetStorageError("保存失败", error);
        }
        catch (OperationCanceledException) { _dirty = true; throw; }
        catch (Exception exception) { _dirty = true; SetStorageError("保存失败", exception); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_saveTimer is not null) { _saveTimer.Stop(); _saveTimer.Tick -= OnSaveTimer; }
        try { _window?.Close(); } catch (Exception exception) { _storageStatus = "关闭趋势窗口失败：" + exception.Message; }
        if (!_loading) CompleteSaves();
    }

    internal IReadOnlyList<HistoryDevice> Devices => _series.Values.Where(s => s.Samples.Count > 0)
        .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(s => new HistoryDevice(s.IdHash, s.Name, s.Samples)).ToArray();
    internal string Status => _storageStatus + "\n" + _scanStatus;

    internal void ClearHistory()
    {
        if (_disposed) return;
        _series.Clear();
        _generation++;
        _discardLoaded = true;
        _readFailed = false;
        MarkDirty();
        _storageStatus = "历史已在界面清空，正在保存清空结果…";
        _ = FlushAsync(CancellationToken.None);
        RefreshWindow();
    }

    private static bool ContainsCache(string text) => text.Contains("缓存", StringComparison.Ordinal) || text.Contains("cache", StringComparison.OrdinalIgnoreCase);
    private static string SafeName(DeviceStatus device)
    {
        var name = device.Name;
        foreach (var identifier in new[] { device.Id, device.InstanceId, device.BluetoothAddress, device.ContainerId, device.ReceiverContainerId })
            if (!string.IsNullOrWhiteSpace(identifier) && identifier.Length >= 6) name = name.Replace(identifier, "", StringComparison.OrdinalIgnoreCase);
        return HistoryStore.DisplayName(name);
    }
    private void Prune(DateTimeOffset now)
    {
        foreach (var series in _series.Values.ToArray())
        {
            int expired = series.Samples.TakeWhile(s => s.At < now - HistoryStore.Retention).Count();
            int remove = Math.Max(expired, series.Samples.Count - HistoryStore.MaxSamples);
            if (remove > 0) { series.Samples.RemoveRange(0, remove); MarkDirty(); }
            if (series.Samples.Count == 0) { _series.Remove(series.IdHash); MarkDirty(); }
        }
    }
    private HistoryDocument Snapshot() => new(_series.Values.Select(s => new HistoryDevice(s.IdHash, s.Name, s.Samples.ToArray())).ToArray());
    private void MarkDirty() { _revision++; _dirty = true; }
    private void OnSaveTimer(object? sender, EventArgs args)
    {
        Prune(DateTimeOffset.UtcNow);
        QueueSave();
    }
    private void QueueSave()
    {
        if (_loading || _readFailed || !_dirty || _disposed) return;
        var request = new SaveRequest(Snapshot(), _generation, _revision);
        if (_saves.Writer.TryWrite(request) && _revision == request.Revision) _dirty = false;
    }

    private async Task StorageLoopAsync()
    {
        try
        {
            await using var lease = await HistoryStore.AcquireLeaseAsync(_filePath);
            HistoryDocument loaded = new([]);
            Exception? readError = null;
            try { loaded = await HistoryStore.LoadAsync(_filePath); }
            catch (Exception exception) { readError = exception; }
            await _dispatcher!.InvokeAsync(() => CompleteLoad(loaded, readError));
            await foreach (var request in _saves.Reader.ReadAllAsync())
            {
                Exception? saveError = null;
                try { await HistoryStore.SaveAsync(_filePath, request.Document); }
                catch (Exception exception) { saveError = exception; }
                request.Completion?.TrySetResult(saveError);
                Post(() =>
                {
                    if (request.Generation != _generation) return;
                    if (saveError is not null) { _dirty = true; SetStorageError("保存失败", saveError); }
                    else { _storageStatus = "已保存到本机；最近一次保存 " + DateTime.Now.ToString("HH:mm:ss") + "。"; RefreshWindow(); }
                });
            }
        }
        catch (Exception exception)
        {
            _loaded.TrySetResult();
            _saves.Writer.TryComplete();
            while (_saves.Reader.TryRead(out var pending)) pending.Completion?.TrySetResult(exception);
            Post(() => { _loading = false; _readFailed = true; SetStorageError("历史存储已停止", exception); });
        }
    }
    private void CompleteLoad(HistoryDocument document, Exception? error)
    {
        if (!_discardLoaded)
        {
            foreach (var saved in document.Devices)
            {
                if (!_series.TryGetValue(saved.IdHash, out var current))
                {
                    if (_series.Count >= HistoryStore.MaxDevices) break;
                    current = new(saved.IdHash, saved.Name);
                    _series.Add(saved.IdHash, current);
                }
                var combined = saved.Samples.Concat(current.Samples).OrderBy(s => s.At).DistinctBy(s => s.At).TakeLast(HistoryStore.MaxSamples).ToArray();
                current.Samples.Clear();
                current.Samples.AddRange(combined);
                if (combined.Length > 0 && current.LastObservedAt < combined[^1].At) current.LastObservedAt = combined[^1].At;
            }
        }
        _loading = false;
        _readFailed = error is not null && !_discardLoaded;
        if (_readFailed) SetStorageError("读取历史失败；原文件未覆盖，可备份后清空历史重试", error!);
        else { Prune(DateTimeOffset.UtcNow); MarkDirty(); _storageStatus = "本地历史已就绪。"; }
        _loaded.TrySetResult();
        if (_disposed) CompleteSaves();
        else { QueueSave(); RefreshWindow(); }
    }
    private void CompleteSaves()
    {
        if (!_dirty || _readFailed || string.IsNullOrEmpty(_filePath)) { _saves.Writer.TryComplete(); return; }
        var request = new SaveRequest(Snapshot(), _generation, _revision);
        if (_saves.Writer.TryWrite(request)) { if (_revision == request.Revision) _dirty = false; _saves.Writer.TryComplete(); }
        else _ = EnqueueLastAsync(request);
    }
    private async Task EnqueueLastAsync(SaveRequest request)
    {
        try { await _saves.Writer.WriteAsync(request).ConfigureAwait(false); }
        catch (ChannelClosedException) { }
        finally { _saves.Writer.TryComplete(); }
    }
    private void Post(Action action)
    {
        try
        {
            if (_dispatcher is { HasShutdownStarted: false })
                _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
        }
        catch (InvalidOperationException) { }
    }
    private void SetStorageError(string prefix, Exception exception)
    {
        _storageStatus = prefix + "：" + exception.Message;
        RefreshWindow();
    }
    private void RefreshWindow()
    {
        try { _window?.Refresh(); }
        catch (Exception exception) { _storageStatus = "无法刷新趋势窗口：" + exception.Message; }
    }
}
