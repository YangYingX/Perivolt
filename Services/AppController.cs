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
    private readonly DesktopWidgetManager widgets;
    private readonly DeviceArrivalService? arrivals;
    private TaskbarBatteryWindow? taskbar;
    private bool exiting;
    private bool exitRequested;
    private bool installingUpdate;
    private Task<PowerHub.Plugins.PluginOutput>? pendingPluginEvaluation;
    public SettingsStore Store { get; }
    public AppSettings Settings { get; }
    public PluginHost Plugins { get; }
    public ThemeBackdrop Backdrop { get; } = new();
    public UpdateService Updates { get; } = new();
    public string? UpdateInstallDirectory { get; }
    public DiagnosticUploadService DiagnosticReports { get; }
    public AutomationService Automation { get; }
    private bool startupEnabled;
    private ScanResult? rawScan;
    public ScanResult? LastScan { get; private set; }
    public IReadOnlyList<DeviceStatus> Devices => LastScan?.Devices ?? [];
    public int ViewRevision { get; private set; }
    public List<string> Diagnostics { get; } = [];
    public bool IsScanning { get; private set; }
    internal bool IsExiting => exiting || exitRequested;
    public string StatusText { get; private set; } = L.T("正在准备设备监测…");
    public event Action? Changed;

    public AppController(string dataDirectory, ThemeService themeService, AppSettings? settings = null, bool preview = false)
    {
        Store = new SettingsStore(dataDirectory);
        Settings = settings ?? Store.Load();
        widgets = new DesktopWidgetManager(Settings, () => ApplySettings(), () => ShowPanel("widgets"));
        if (!preview) arrivals = new DeviceArrivalService(Settings, id => ShowPanel("appearance")?.ShowDeviceSettings(id));
        L.SetLanguage(Settings.Language);
        DiagnosticReports = new DiagnosticUploadService(dataDirectory);
        StatusText = L.T("正在准备设备监测…");
        previewMode = preview;
        UpdateInstallDirectory = preview ? null : UpdateInstaller.FindInstalledDirectory();
        themes = themeService;
        Plugins = new PluginHost(Program.PluginDirectory, Settings);
        if (!previewMode) Plugins.ConfigureInteractive(dataDirectory, Dispatcher.CurrentDispatcher, SelectTaskbarDevice,
            target => new TaskbarBatteryWindow(Settings, null, Plugins.CreateTaskbarPopups, Plugins.HandleTaskbarWheel, target),
            () => { if (!IsExiting) { taskbar?.ClosePluginPopup(); taskbar?.UpdatePlacement(); } });
        Plugins.InteractiveChanged += OnInteractiveChanged;
        monitor = new BatteryMonitorService(Plugins);
        if (!previewMode) Settings.StartWithWindows = startupEnabled = StartupService.IsEnabled();
        ApplyTheme();
        BatteryPalette.Apply(Settings);
        themes.SystemThemeChanged += OnSystemThemeChanged;
        if (Store.LastError is { } error) Diagnostics.Add(error);
        if (!previewMode) tray = new TrayService(() => ShowPanel(), () => _ = RefreshAsync(), Exit, () => ShowPanel("widgets"), () => ShowPanel("settings"), Settings, () => ApplySettings(), id => ApplyDesktopScene(id), () => ShowPanel("automation"));
        Automation = new AutomationService(Settings, dataDirectory, ApplyAutomationDesktop, CaptureAutomationDesktop,
            enabled => { Settings.NotificationsEnabled = enabled; ApplySettings(); },
            message => tray?.ShowPluginNotification(L.T("外设自动化"), message), preview);
        Automation.Changed += OnAutomationChanged;
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
        if (IsExiting) return;
        if (!previewMode)
        {
            timer.Start();
            Updates.Configure(Settings.AutoCheckUpdates);
            DiagnosticReports.Configure(Settings.AutoReportDiagnostics);
        }
        SyncWidget();
        SyncTaskbar();
        Automation.Start();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsScanning || IsExiting) return;
        IsScanning = true;
        StatusText = L.T("正在更新设备电量…");
        Changed?.Invoke();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var result = await monitor.ScanAsync(true, null, lifetime.Token);
            if (IsExiting) return;
            rawScan = result;
            if (!previewMode) DiagnosticReports.ObserveScan(result);
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
            if (!previewMode && Plugins.HasInteractivePlugins) Plugins.UpdateInteractive(PluginDevices(result.Devices), result.ScannedAt);
            widgets.UpdateDevices(result.Devices);
            arrivals?.Observe(result);
            Automation.ObserveDevices(result);
            taskbar?.UpdateDevices(result.Devices);
            var effects = await EvaluatePluginsAsync(result.Devices);
            if (IsExiting) return;
            tray?.Update(result, startedAt, effects.SuppressLowBatteryAlerts);
            if (Settings.NotificationsEnabled && !effects.SuppressLowBatteryAlerts && effects.Notifications is not null)
                foreach (var notification in effects.Notifications.Take(3)) tray?.ShowPluginNotification(notification.Title, notification.Message);
        }
        catch (OperationCanceledException) when (IsExiting) { }
        catch (Exception error)
        {
            if (IsExiting) return;
            LastScan = null;
            rawScan = null;
            ViewRevision++;
            if (!previewMode && Plugins.HasInteractivePlugins) Plugins.UpdateInteractive([], DateTimeOffset.UtcNow);
            widgets.UpdateDevices([]);
            arrivals?.ResetBaseline();
            Automation.DeviceScanFailed();
            taskbar?.UpdateDevices([]);
            StatusText = L.T("本次更新未完成，可稍后重试");
            Diagnostics.Add(error.ToString());
        }
        finally
        {
            IsScanning = false;
            if (!IsExiting) Changed?.Invoke();
        }
    }

    private async Task<PowerHub.Plugins.PluginOutput> EvaluatePluginsAsync(IReadOnlyList<DeviceStatus> devices)
    {
        // A slow extension must not block the dispatcher or accumulate a new worker each scan.
        if (pendingPluginEvaluation is { IsCompleted: false }) return new();
        var snapshot = PluginDevices(devices);
        pendingPluginEvaluation = Task.Run(() => Plugins.Evaluate(snapshot, DateTimeOffset.Now));
        try { return await pendingPluginEvaluation.WaitAsync(TimeSpan.FromMilliseconds(500), lifetime.Token); }
        catch (TimeoutException) { Diagnostics.Add(L.T("实用插件计算超过 500 ms；本轮跳过结果，等待现有任务结束。")); return new(); }
        finally { if (pendingPluginEvaluation.IsCompleted) pendingPluginEvaluation = null; }
    }

    private DeviceStatus[] PluginDevices(IReadOnlyList<DeviceStatus> devices) => devices.Select(device =>
        {
            var name = Settings.GetDeviceName(device);
            return name != device.Name ? device with { Name = name } : device;
        }).ToArray();

    private void SelectTaskbarDevice(string? id)
    {
        if (IsExiting || id is not null && !Devices.Any(device => device.Id == id && device.ConnectionKnown && device.IsConnected && device.HasBattery)) return;
        Settings.TaskbarDeviceId = id;
        ApplySettings();
    }

    private void OnInteractiveChanged() => taskbar?.ClosePluginPopup();

    public MainWindow? ShowPanel(string page = "devices")
    {
        if (IsExiting) return null;
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
        if (IsExiting) return;
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
        if (!previewMode && Plugins.HasInteractivePlugins) Plugins.UpdateInteractive(PluginDevices(Devices), LastScan?.ScannedAt ?? DateTimeOffset.UtcNow);
        timer.Interval = TimeSpan.FromSeconds(Settings.PollIntervalSeconds);
        tray?.ApplySettings();
        arrivals?.ApplySettings();
        Automation.ApplySettings();
        if (!previewMode) Updates.Configure(Settings.AutoCheckUpdates);
        if (!previewMode) DiagnosticReports.Configure(Settings.AutoReportDiagnostics);
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
        Backdrop.Apply(Settings, selectedTheme);
    }

    private void OnSystemThemeChanged()
    {
        if (IsExiting) return;
        ViewRevision++;
        widgets.Apply(Devices);
        taskbar?.UpdateDevices(Devices);
        Changed?.Invoke();
    }

    private void SyncWidget()
    {
        widgets.Apply(Devices);
    }

    public DesktopWidgetWindow? Widget => widgets.PreviewWindow;

    private void OnAutomationChanged() { if (!IsExiting) Changed?.Invoke(); }
    private DesktopScene CaptureAutomationDesktop()
    {
        widgets.FlushPositions();
        return DesktopScene.Capture(Settings, "");
    }
    private void ApplyAutomationDesktop(DesktopScene scene)
    {
        window?.FlushPendingEdits();
        widgets.FlushPositions();
        scene.Apply(Settings);
        if (!IsExiting) { ApplySettings(); widgets.RestorePositions(); }
    }
    public IReadOnlyList<string> SaveAutomationRule(AutomationRule rule)
    {
        if (IsExiting) return [L.T("自动化中心已停止。")];
        var copy = rule.Clone(); copy.Normalize();
        if (!Guid.TryParseExact(copy.Id, "N", out _)) return [L.T("规则标识无效，请重新创建规则。")];
        var errors = Automation.Validate(copy);
        if (errors.Count > 0) return errors;
        var index = Settings.AutomationRules.FindIndex(existing => existing.Id == copy.Id);
        if (index < 0 && Settings.AutomationRules.Count >= AutomationRule.MaximumCount) return [L.T("最多保存 32 条规则。")];
        if (index < 0) Settings.AutomationRules.Add(copy); else Settings.AutomationRules[index] = copy;
        ApplySettings(); return [];
    }
    public void DeleteAutomationRule(string id)
    {
        if (!IsExiting && Settings.AutomationRules.RemoveAll(rule => rule.Id == id) > 0) ApplySettings();
    }
    public void SetAutomationRuleEnabled(string id, bool enabled)
    {
        if (IsExiting || Settings.AutomationRules.FirstOrDefault(rule => rule.Id == id) is not { } rule) return;
        rule.Enabled = enabled; ApplySettings();
    }

    public void RecoverWidgets()
    {
        Settings.WidgetEnabled = true;
        Settings.WidgetLocked = false;
        SyncWidget();
        widgets.Recover();
        ApplySettings();
    }

    public string? SaveDesktopScene(string name, string? replaceId = null)
    {
        if (IsExiting || string.IsNullOrWhiteSpace(name)) return null;
        var index = replaceId is null ? -1 : Settings.DesktopScenes.FindIndex(scene => scene.Id == replaceId);
        if (replaceId is not null && index < 0 || index < 0 && Settings.DesktopScenes.Count >= DesktopScene.MaximumCount) return null;
        window?.FlushPendingEdits();
        widgets.FlushPositions();
        var scene = DesktopScene.Capture(Settings, name, replaceId);
        scene.Normalize();
        if (index < 0) Settings.DesktopScenes.Add(scene);
        else Settings.DesktopScenes[index] = scene;
        ApplySettings();
        return scene.Id;
    }

    public bool ApplyDesktopScene(string id)
    {
        if (IsExiting || Settings.DesktopScenes.FirstOrDefault(scene => scene.Id == id) is not { } scene) return false;
        window?.FlushPendingEdits();
        widgets.FlushPositions();
        scene.Apply(Settings);
        ApplySettings();
        widgets.RestorePositions();
        return true;
    }

    public void DeleteDesktopScene(string id)
    {
        if (!IsExiting && Settings.DesktopScenes.RemoveAll(scene => scene.Id == id) > 0) ApplySettings();
    }
    public string? TaskbarError => taskbar?.LastError;

    private void SyncTaskbar()
    {
        if (previewMode) return;
        if (!Settings.TaskbarEnabled) { taskbar?.Suspend(); return; }
        taskbar ??= new TaskbarBatteryWindow(Settings, () => ApplySettings(), Plugins.CreateTaskbarPopups, Plugins.HandleTaskbarWheel);
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

    public async Task DownloadAndInstallUpdateAsync()
    {
        if (previewMode || IsExiting || installingUpdate) return;
        installingUpdate = true;
        try
        {
            var installer = await Updates.DownloadInstallerAsync();
            if (installer is null || IsExiting || !Updates.IsEnabled) return;
            try
            {
                window?.FlushPendingEdits();
                saveTimer.Stop();
                Store.Save(Settings);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Updates.InstallationFailed(true);
                return;
            }
            if (IsExiting || !Updates.IsEnabled) return;
            try { UpdateInstaller.Start(installer, UpdateInstallDirectory, Path.GetDirectoryName(Store.FilePath)!); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException)
            {
                Updates.InstallationFailed(false);
                return;
            }
            Exit();
        }
        finally { installingUpdate = false; }
    }

    public async Task ExportAsync(string path)
    {
        var snapshot = new { Application = "PowerHub " + UpdateService.DisplayVersion, ExportedAt = DateTimeOffset.Now, LastScan?.ScannedAt, Devices, Diagnostics };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, Program.JsonOptions));
    }

    public DiagnosticReport? CreateDiagnosticReport() => rawScan is null ? null :
        DiagnosticReportBuilder.Build(rawScan, UpdateService.DisplayVersion);

    public async void Exit()
    {
        if (IsExiting) return;
        exitRequested = true;
        timer.Stop();
        saveTimer.Stop();
        try
        {
            lifetime.Cancel();
            using (var automationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                try { await Automation.StopAsync(automationDeadline.Token); }
                catch (OperationCanceledException) { Diagnostics.Add(L.T("退出时自动化恢复或记录保存未完成。")); }
                catch (Exception error) { StartupDiagnostics.Write(error); }
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Plugins.FlushInteractiveAsync(deadline.Token);
        }
        catch (OperationCanceledException) { Diagnostics.Add("插件保存超时，继续退出。"); }
        catch (Exception error) { StartupDiagnostics.Write(error); }
        finally
        {
            try { Dispose(); }
            finally { System.Windows.Application.Current.Shutdown(); }
        }
    }

    public void Dispose()
    {
        if (exiting) return;
        exiting = true;
        timer.Stop();
        saveTimer.Stop();
        themes.SystemThemeChanged -= OnSystemThemeChanged;
        Plugins.InteractiveChanged -= OnInteractiveChanged;
        Cleanup(lifetime.Cancel);
        Cleanup(() => tray?.Dispose());
        Cleanup(() => window?.CloseImmediately());
        Cleanup(widgets.Close);
        Cleanup(() => arrivals?.Dispose());
        Automation.Changed -= OnAutomationChanged;
        Cleanup(Automation.Dispose);
        Cleanup(() => taskbar?.Close());
        Cleanup(Updates.Dispose);
        Cleanup(DiagnosticReports.Dispose);
        Cleanup(Backdrop.Dispose);
        Cleanup(Plugins.Dispose);
        Cleanup(SaveSettings);

        void Cleanup(Action close)
        {
            try { close(); }
            catch (Exception error)
            {
                Diagnostics.Add(error.ToString());
                StartupDiagnostics.Write(error);
            }
        }
    }
}
