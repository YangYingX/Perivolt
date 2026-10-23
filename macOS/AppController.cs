using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using PowerHub.Mac.Services;
using PowerHub.Models;
using PowerHub.Services;

namespace PowerHub.Mac;

public sealed class AppController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly SettingsStore store;
    private readonly HistoryStore history;
    private readonly MacBatteryProvider provider = new();
    private readonly ReminderPolicy reminders = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new();
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly List<string> diagnostics = [];
    private MainWindow? window;
    private DesktopWidgetWindow? widget;
    private MacTrayService? tray;
    private bool startupEnabled;
    private bool started;
    private readonly bool firstRun;
    public AppSettings Settings { get; }
    public IReadOnlyList<DeviceStatus> Devices { get; private set; } = [];
    public IReadOnlyList<string> Diagnostics => diagnostics;
    public string StatusText { get; private set; } = "";
    public bool IsScanning { get; private set; }
    public bool IsExiting { get; private set; }
    public bool PreviewMode { get; }
    public string DataDirectory { get; }
    public UpdateService Updates { get; }
    public event Action? Changed;

    public AppController(IClassicDesktopStyleApplicationLifetime desktop, string dataDirectory)
    {
        this.desktop = desktop;
        DataDirectory = dataDirectory;
        PreviewMode = Program.Arguments.Contains("--preview") || Program.Arguments.Contains("--screenshot");
        store = new SettingsStore(dataDirectory);
        firstRun = !File.Exists(store.FilePath);
        history = new HistoryStore(dataDirectory);
        Settings = store.Load();
        Settings.Normalize();
        if (store.LastError is { } loadError) diagnostics.Add(loadError);
        if (Program.ArgumentValue(Program.Arguments, "--theme") is { } theme) Settings.Theme = theme;
        if (Program.ArgumentValue(Program.Arguments, "--language") is { } language) Settings.Language = language;
        Settings.Normalize();
        L.SetLanguage(Settings.Language);
        StatusText = L.T("正在准备设备监测…");
        if (!PreviewMode)
        {
            try { startupEnabled = MacPlatform.IsStartupEnabled(); Settings.StartWithWindows = startupEnabled; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { diagnostics.Add(error.Message); }
        }
        Updates = new UpdateService(dataDirectory);
        Updates.Changed += OnUpdateChanged;
        Updates.UpdateAvailable += OnUpdateAvailable;
        timer.Tick += OnTimer;
        saveTimer.Tick += OnSaveTimer;
    }

    public void Start(bool show)
    {
        if (started || IsExiting) return;
        started = true;
        if (!PreviewMode)
        {
            try { tray = new MacTrayService(this); }
            catch (Exception error) { AddDiagnostic(L.T("菜单栏") + ": " + error.Message); }
        }
        ApplySettings(false);
        if (show || firstRun || PreviewMode || !Settings.StartInTray || tray is null) ShowMain();
        if (!PreviewMode) timer.Start();
        _ = RefreshAsync();
    }

    private void OnTimer(object? sender, EventArgs args) => _ = RefreshAsync();
    private void OnSaveTimer(object? sender, EventArgs args) { saveTimer.Stop(); SaveSettings(); }
    private void OnUpdateChanged() { if (!IsExiting) Changed?.Invoke(); }
    private void OnUpdateAvailable(string version)
    {
        if (!PreviewMode && Settings.NotificationsEnabled) _ = NotifyAsync(L.F("发现新版本 v{0}，可下载并安装", version));
    }

    public async Task RefreshAsync()
    {
        if (IsScanning || IsExiting) return;
        IsScanning = true;
        StatusText = L.T("正在更新设备电量…");
        Changed?.Invoke();
        var scanStarted = DateTimeOffset.Now;
        try
        {
            var result = PreviewMode ? PreviewScan() : await provider.ScanAsync(cancellationToken: lifetime.Token);
            if (IsExiting) return;
            Devices = result.Devices.OrderBy(device => Settings.GetDeviceOrder(device.Id)).ToArray();
            diagnostics.Clear();
            diagnostics.AddRange(result.Diagnostics);
            foreach (var device in Devices) GetPreference(device.Id).DisplayName = device.Name;
            if (PreviewMode && Program.Arguments.Contains("--widget-screenshot"))
            {
                Settings.WidgetEnabled = true;
                foreach (var device in Devices.Take(3)) GetPreference(device.Id).Pinned = true;
            }
            StatusText = L.F("已更新 {0:HH:mm} · {1} 台在线", result.ScannedAt.ToLocalTime(), Devices.Count(device => device.ConnectionKnown && device.IsConnected));
            SyncWidget();
            tray?.Refresh();
            if (!PreviewMode)
            {
                if (Settings.HistoryEnabled && await history.AppendAsync(result) is { } historyError) AddDiagnostic(historyError);
                if (IsExiting) return;
                foreach (var device in reminders.Evaluate(result, scanStarted, Settings, DateTimeOffset.Now))
                    _ = NotifyAsync(L.F("{0} 电量 {1}，请及时充电。", Settings.GetDeviceName(device), device.BatteryText));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsExiting) { StatusText = L.T("设备读取失败：") + error.Message; AddDiagnostic(error.Message); }
        }
        finally
        {
            IsScanning = false;
            if (!IsExiting) Changed?.Invoke();
        }
    }

    public void ShowMain()
    {
        if (IsExiting) return;
        if (window is null)
        {
            window = new MainWindow(this);
            window.Closing += (_, _) =>
            {
                if (tray is null && !IsExiting) Dispatcher.UIThread.Post(Exit);
            };
        }
        desktop.MainWindow = window;
        if (Program.ArgumentValue(Program.Arguments, "--page") is { } page) window.Navigate(page);
        if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
        if (!window.IsVisible) window.Show();
        if (!Program.Arguments.Contains("--screenshot")) window.Activate();
    }

    public void ApplySettings(bool persist = true)
    {
        if (IsExiting) return;
        Settings.Normalize();
        L.SetLanguage(Settings.Language);
        if (Application.Current is { } app)
            app.RequestedThemeVariant = Settings.Theme switch
            {
                "Dark" or "Ocean" or "Forest" => ThemeVariant.Dark,
                "Light" or "Lavender" or "Sunset" => ThemeVariant.Light,
                _ => ThemeVariant.Default
            };
        timer.Interval = TimeSpan.FromSeconds(Settings.PollIntervalSeconds);
        if (!PreviewMode && Settings.StartWithWindows != startupEnabled)
        {
            try { MacPlatform.SetStartup(Settings.StartWithWindows); startupEnabled = Settings.StartWithWindows; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { Settings.StartWithWindows = startupEnabled; AddDiagnostic(error.Message); StatusText = L.T("开机启动设置未保存：") + error.Message; }
        }
        Updates.Configure(!PreviewMode && Settings.AutoCheckUpdates);
        SyncWidget();
        tray?.Refresh();
        if (persist && !PreviewMode) { saveTimer.Stop(); saveTimer.Start(); }
        Changed?.Invoke();
    }

    public DevicePreference GetPreference(string id)
    {
        if (!Settings.Devices.TryGetValue(id, out var preference)) Settings.Devices[id] = preference = new();
        return preference;
    }

    public void SetPinned(string id, bool value)
    {
        GetPreference(id).Pinned = value;
        if (value) Settings.WidgetEnabled = true;
        ApplySettings();
    }

    private void SyncWidget()
    {
        if (!Settings.WidgetEnabled || !Devices.Any(device => device.ConnectionKnown && device.IsConnected && Settings.IsPinned(device.Id)))
        { widget?.Hide(); return; }
        widget ??= new DesktopWidgetWindow(this);
        widget.Refresh();
        if (!widget.IsVisible) widget.Show();
    }

    public async Task ExportAsync(string path)
    {
        var snapshot = new { Application = "Perivolt macOS " + UpdateService.DisplayVersion, ExportedAt = DateTimeOffset.Now, Devices, Diagnostics };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, Program.JsonOptions));
    }

    public void OpenDataDirectory()
    {
        if (!PreviewMode) { Directory.CreateDirectory(DataDirectory); MacPlatform.Open(DataDirectory); }
    }

    private async Task NotifyAsync(string message)
    {
        try { await MacPlatform.NotifyAsync(L.AppName, message, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!IsExiting) { AddDiagnostic(error.Message); Changed?.Invoke(); } }
    }

    private void AddDiagnostic(string message)
    {
        diagnostics.Add(message);
        if (diagnostics.Count > 200) diagnostics.RemoveRange(0, diagnostics.Count - 200);
    }

    private void SaveSettings()
    {
        if (PreviewMode) return;
        try { store.Save(Settings); }
        catch (IOException error) { AddDiagnostic(error.Message); StatusText = error.Message; Changed?.Invoke(); }
    }

    internal async Task CaptureAsync(string path)
    {
        try
        {
            ShowMain();
            await Task.Delay(500);
            var target = Program.Arguments.Contains("--widget-screenshot") ? (Avalonia.Controls.Window?)widget : window;
            if (target is null) throw new InvalidOperationException("The requested preview window is not available.");
            var scale = target.RenderScaling;
            using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(target.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(target.Bounds.Height * scale))), new Vector(96 * scale, 96 * scale));
            bitmap.Render(target);
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            bitmap.Save(fullPath);
            Exit();
        }
        catch (Exception error) { Console.Error.WriteLine(error); Dispose(); desktop.Shutdown(1); }
    }

    private static ScanResult PreviewScan()
    {
        var now = DateTimeOffset.Now;
        DeviceStatus Device(string id, string name, string category, int? percent, bool? charging = false) => new()
        {
            Id = id, Name = name, Category = category, Connection = "蓝牙", ConnectionKnown = true, IsConnected = true,
            BatteryPercent = percent, IsCharging = charging, Source = "Preview", UpdatedAt = now,
            Message = "预览样本；未访问本机外设"
        };
        return new([Device("preview-keyboard", "Magic Keyboard", "键盘", 72), Device("preview-mouse", "MX Master", "鼠标", 34),
            Device("preview-headphones", "AirPods · 左耳", "耳机", 16), Device("preview-case", "AirPods · 充电盒", "耳机", null, null)],
            ["macOS 开发预览：此窗口使用样本数据，不读取设备、不发网络请求、不修改登录启动。", "真实设备读取、菜单栏、通知和登录启动仍需在 Mac 上验证。"], now);
    }

    public void Exit()
    {
        if (IsExiting) return;
        Dispose();
        desktop.Shutdown();
    }

    public void Dispose()
    {
        if (IsExiting) return;
        IsExiting = true;
        timer.Stop(); timer.Tick -= OnTimer;
        saveTimer.Stop(); saveTimer.Tick -= OnSaveTimer;
        lifetime.Cancel();
        Updates.Changed -= OnUpdateChanged;
        Updates.UpdateAvailable -= OnUpdateAvailable;
        Updates.Dispose();
        tray?.Dispose();
        widget?.CloseForExit();
        window?.CloseForExit();
        SaveSettings();
        lifetime.Dispose();
    }
}
