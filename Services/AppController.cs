using PowerHub.Services;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using PowerHub.Models;

namespace PowerHub.Services;

public sealed class AppController : IDisposable
{
    private readonly BatteryMonitorService monitor;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new();
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ThemeService themes;
    private readonly TrayService? tray;
    private readonly bool previewMode;
    private MainWindow? window;
    private DesktopWidgetWindow? widget;
    private TaskbarBatteryWindow? taskbar;
    private bool exiting;
    private Task<PowerHub.Plugins.PluginOutput>? pendingPluginEvaluation;
    public SettingsStore Store { get; }
    public AppSettings Settings { get; }
    public PluginHost Plugins { get; }
    public UpdateService Updates { get; } = new();
    private bool startupEnabled;
    private ScanResult? rawScan;
    public ScanResult? LastScan { get; private set; }
    public IReadOnlyList<DeviceStatus> Devices => LastScan?.Devices ?? [];
    public int ViewRevision { get; private set; }
    public List<string> Diagnostics { get; } = [];
    public bool IsScanning { get; private set; }
    public string StatusText { get; private set; } = L.T("正在准备设备监测…");
    public event Action? Changed;

    public AppController(string dataDirectory, ThemeService themeService, AppSettings? settings = null, bool preview = false)
    {
        Store = new SettingsStore(dataDirectory);
        Settings = settings ?? Store.Load();
        L.SetLanguage(Settings.Language);
        StatusText = L.T("正在准备设备监测…");
        previewMode = preview;
        themes = themeService;
        Plugins = new PluginHost(Program.PluginDirectory, Settings);
        monitor = new BatteryMonitorService(Plugins);
        if (!previewMode) Settings.StartWithWindows = startupEnabled = StartupService.IsEnabled();
        ApplyTheme();
        BatteryPalette.Apply(Settings);
        themes.SystemThemeChanged += OnSystemThemeChanged;
        if (Store.LastError is { } error) Diagnostics.Add(error);
        if (!previewMode) tray = new TrayService(() => ShowPanel(), () => _ = RefreshAsync(), Exit, () => ShowPanel("widgets"), () => ShowPanel("settings"), Settings, () => ApplySettings());
        Updates.UpdateAvailable += version =>
        {
            if (Settings.NotificationsEnabled) tray?.ShowPluginNotification(L.T("PowerHub 有新版本"), L.F("v{0} 已发布，可在设置的通用选项中查看更新。", version));
        };
        timer.Tick += (_, _) => _ = RefreshAsync();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSettings(); };
        timer.Interval = TimeSpan.FromSeconds(Settings.PollIntervalSeconds);
    }

    public async Task StartAsync()
    {
        if (!previewMode) { timer.Start(); Updates.Configure(Settings.AutoCheckUpdates); }
        SyncWidget();
        SyncTaskbar();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsScanning || exiting) return;
        IsScanning = true;
        StatusText = L.T("正在更新设备电量…");
        Changed?.Invoke();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var result = await monitor.ScanAsync(true, null, lifetime.Token);
            if (exiting) return;
            rawScan = result;
            foreach (var device in result.Devices)
            {
                if (!Settings.Devices.TryGetValue(device.Id, out var remembered)) Settings.Devices[device.Id] = remembered = new();
                remembered.DisplayName = device.Name;
            }
            result = DeviceIdentity.ApplyLinks(result, Settings);
            LastScan = result;
            ViewRevision++;
            Diagnostics.Clear();
            Diagnostics.AddRange(result.Diagnostics);
            StatusText = L.F("已更新 {0:HH:mm} · {1} 台在线", result.ScannedAt.ToLocalTime(), result.Devices.Count(x => x.IsConnected));
            widget?.UpdateDevices(result.Devices);
            taskbar?.UpdateDevices(result.Devices);
            var effects = await EvaluatePluginsAsync(result.Devices);
            if (exiting) return;
            tray?.Update(result, startedAt, effects.SuppressLowBatteryAlerts);
            if (Settings.NotificationsEnabled && !effects.SuppressLowBatteryAlerts && effects.Notifications is not null)
                foreach (var notification in effects.Notifications.Take(3)) tray?.ShowPluginNotification(notification.Title, notification.Message);
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception error)
        {
            LastScan = null;
            rawScan = null;
            ViewRevision++;
            widget?.UpdateDevices([]);
            taskbar?.UpdateDevices([]);
            StatusText = L.T("本次更新未完成，可稍后重试");
            Diagnostics.Add(error.ToString());
        }
        finally
        {
            IsScanning = false;
            if (!exiting) Changed?.Invoke();
        }
    }

    private async Task<PowerHub.Plugins.PluginOutput> EvaluatePluginsAsync(IReadOnlyList<DeviceStatus> devices)
    {
        // A slow extension must not block the dispatcher or accumulate a new worker each scan.
        if (pendingPluginEvaluation is { IsCompleted: false }) return new();
        var snapshot = devices.Select(device =>
        {
            var name = Settings.GetDeviceName(device);
            return name != device.Name ? device with { Name = name } : device;
        }).ToArray();
        pendingPluginEvaluation = Task.Run(() => Plugins.Evaluate(snapshot, DateTimeOffset.Now));
        try { return await pendingPluginEvaluation.WaitAsync(TimeSpan.FromMilliseconds(500), lifetime.Token); }
        catch (TimeoutException) { Diagnostics.Add(L.T("实用插件计算超过 500 ms；本轮跳过结果，等待现有任务结束。")); return new(); }
        finally { if (pendingPluginEvaluation.IsCompleted) pendingPluginEvaluation = null; }
    }

    public MainWindow ShowPanel(string page = "devices")
    {
        if (window is null)
        {
            window = new MainWindow(this);
            window.Closed += (_, _) => window = null;
        }
        window.Navigate(page);
        window.ShowWithTransition();
        return window;
    }

    public void TogglePin(string id)
    {
        var preference = GetPreference(id);
        preference.Pinned = !preference.Pinned;
        if (preference.Pinned) Settings.WidgetEnabled = true;
        ApplySettings();
    }

    public DevicePreference GetPreference(string id)
    {
        if (!Settings.Devices.TryGetValue(id, out var preference))
            Settings.Devices[id] = preference = new DevicePreference();
        preference.DisplayName = Devices.FirstOrDefault(x => x.Id == id)?.Name ?? preference.DisplayName;
        return preference;
    }

    public void ToggleWidget()
    {
        Settings.WidgetEnabled = !Settings.WidgetEnabled;
        if (Settings.WidgetEnabled && !Settings.Devices.Values.Any(x => x.Pinned))
            foreach (var device in Devices.Where(x => x.HasBattery).OrderByDescending(x => x.IsConnected).Take(3)) GetPreference(device.Id).Pinned = true;
        ApplySettings();
    }

    public void ApplySettings(bool persist = true)
    {
        Settings.Normalize();
        var languageChanged = L.SetLanguage(Settings.Language);
        if (rawScan is not null) LastScan = DeviceIdentity.ApplyLinks(rawScan, Settings);
        if (languageChanged)
            StatusText = IsScanning ? L.T("正在更新设备电量…") : LastScan is { } scan
                ? L.F("已更新 {0:HH:mm} · {1} 台在线", scan.ScannedAt.ToLocalTime(), scan.Devices.Count)
                : L.T("正在准备设备监测…");
        ApplyTheme();
        BatteryPalette.Apply(Settings);
        ViewRevision++;
        timer.Interval = TimeSpan.FromSeconds(Settings.PollIntervalSeconds);
        tray?.ApplySettings();
        if (!previewMode) Updates.Configure(Settings.AutoCheckUpdates);
        SyncWidget();
        SyncTaskbar();
        if (!previewMode && startupEnabled != Settings.StartWithWindows)
        {
            try { StartupService.SetEnabled(Settings.StartWithWindows); startupEnabled = Settings.StartWithWindows; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            {
                Settings.StartWithWindows = startupEnabled;
                StatusText = L.T("开机启动设置未保存：") + error.Message;
                Diagnostics.Add(StatusText);
            }
        }
        if (persist && !previewMode) { saveTimer.Stop(); saveTimer.Start(); }
        Changed?.Invoke();
    }

    private void ApplyTheme()
    {
        var selectedTheme = Plugins.ActiveTheme;
        if (Settings.Theme.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase) && selectedTheme is null)
            Settings.Theme = "System";
        themes.Apply(Settings.Theme, selectedTheme);
    }

    private void OnSystemThemeChanged()
    {
        ViewRevision++;
        widget?.ApplySettings();
        widget?.UpdateDevices(Devices);
        taskbar?.UpdateDevices(Devices);
        Changed?.Invoke();
    }

    private void SyncWidget()
    {
        if (!Settings.WidgetEnabled) { widget?.Hide(); return; }
        widget ??= new DesktopWidgetWindow(Settings, () => ApplySettings(), () => ShowPanel());
        widget.ApplySettings();
        widget.UpdateDevices(Devices);
        if (!widget.IsVisible) widget.Show();
    }

    public DesktopWidgetWindow? Widget => widget;

    private void SyncTaskbar()
    {
        if (previewMode) return;
        if (!Settings.TaskbarEnabled) { taskbar?.Suspend(); return; }
        taskbar ??= new TaskbarBatteryWindow(Settings);
        taskbar.UpdateDevices(Devices);
    }

    public void SaveSettings()
    {
        if (previewMode) return;
        try { Store.Save(Settings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText = L.T("设置暂未保存，请检查文件夹权限");
            Diagnostics.Add(error.Message);
            Changed?.Invoke();
        }
    }

    public async Task ExportAsync(string path)
    {
        var snapshot = new { Application = "PowerHub " + UpdateService.CurrentVersion, ExportedAt = DateTimeOffset.Now, LastScan?.ScannedAt, Devices, Diagnostics };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, Program.JsonOptions));
    }

    public void Exit()
    {
        if (exiting) return;
        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (exiting) return;
        exiting = true;
        window?.CloseImmediately();
        taskbar?.Close();
        timer.Stop();
        saveTimer.Stop();
        lifetime.Cancel();
        Updates.Dispose();
        themes.SystemThemeChanged -= OnSystemThemeChanged;
        SaveSettings();
        widget?.CloseForExit();
        tray?.Dispose();
        Plugins.Dispose();
    }
}
