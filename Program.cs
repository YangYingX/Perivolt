using System.IO;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerHub.Models;
using PowerHub.Services;

namespace PowerHub;

public static class Program
{
    public static string PluginDirectory => Path.Combine(
        string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "PowerHub", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Environment.ProcessPath)! : AppContext.BaseDirectory, "plugins");
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-check"))
        {
            AttachConsole(unchecked((uint)-1));
            try
            {
                var unknown = new DeviceStatus();
                var known = unknown with { BatteryPercent = 20, IsCharging = true, IsConnected = true };
                if (unknown.HasBattery || unknown.BatteryText != "--" || !known.HasBattery || known.BatteryText != "20%" || known.ChargingText != "正在充电")
                    throw new InvalidOperationException("设备模型断言失败");
                var merged = BatteryMonitorService.MergeDevices([
                    new() { Id = "pnp", ContainerId = "one", BatteryPercent = 44, Source = "Windows 电量属性" },
                    new() { Id = "classic", ContainerId = "one", BluetoothAddress = "112233445566", Connection = "蓝牙", ConnectionKnown = true, IsConnected = false },
                    new() { Id = "ble", BluetoothAddress = "112233445566", Connection = "蓝牙 BLE", ConnectionKnown = true, IsConnected = true, BatteryPercent = 0, Source = "BLE Battery Service" }
                ]);
                if (merged.Count != 1 || merged[0].BatteryPercent != 0 || !merged[0].IsConnected || !merged[0].ConnectionKnown)
                    throw new InvalidOperationException("跨接口合并或零电量断言失败");
                var distinct = BatteryMonitorService.MergeDevices([
                    new() { Id = "mouse1", ContainerId = "first", VendorId = 123, ProductId = 456 },
                    new() { Id = "mouse2", ContainerId = "second", VendorId = 123, ProductId = 456 }
                ]);
                if (distinct.Count != 2) throw new InvalidOperationException("同型号的不同设备被错误合并");
                var disconnected = BatteryMonitorService.MergeDevices([
                    new() { Id = "hid", ContainerId = "third", ConnectionKnown = false, IsConnected = false },
                    new() { Id = "aep", ContainerId = "third", Connection = "蓝牙", ConnectionKnown = true, IsConnected = false }
                ]).Single();
                if (!disconnected.ConnectionKnown || disconnected.IsConnected) throw new InvalidOperationException("断开连接状态断言失败");
                if (Marshal.SizeOf<Native.HidNative.Caps>() != 64 || Marshal.SizeOf<Native.HidNative.ValueCaps>() != 72 || Marshal.SizeOf<Native.HidNative.ButtonCaps>() != 72)
                    throw new InvalidOperationException("Windows HID ABI 大小断言失败");
                CheckSettingsAndReminders();
                Console.WriteLine("PowerHub self-check passed: model, identity, HID ABI, settings persistence, reminder rules.");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        if (args.Contains("--scan"))
        {
            AttachConsole(unchecked((uint)-1));
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var scan = ScanAsync(args);
                if (!scan.IsCompleted)
                {
                    var frame = new DispatcherFrame();
                    _ = scan.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                    Dispatcher.PushFrame(frame);
                }
                return scan.GetAwaiter().GetResult();
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }

        var screenshotPath = ArgumentValue(args, "--screenshot");
        var widgetScreenshot = ArgumentValue(args, "--widget-screenshot");
        var capturing = screenshotPath is not null || widgetScreenshot is not null;
        using var showSignal = capturing ? null : new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PowerHub.ShowPanel");
        using var instanceMutex = capturing ? null : new Mutex(false, @"Local\PowerHub.TrayApp", out _);
        if (!capturing && !instanceMutex!.WaitOne(0))
        {
            showSignal!.Set();
            return 0;
        }
        string dataDirectory;
        try
        {
            var executableDirectory = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "PowerHub", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(Environment.ProcessPath)! : AppContext.BaseDirectory;
            dataDirectory = AppPaths.ResolveDataDirectory(ArgumentValue(args, "--data-dir"), capturing, FindWorkspaceRoot(),
                executableDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (capturing) Console.Error.WriteLine(error.Message);
            else System.Windows.MessageBox.Show(L.F("无法准备设置目录：{0}", error.Message), L.AppName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
        var application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/PowerHub;component/Themes/Controls.xaml", UriKind.Relative) });
        using var themes = new ThemeService();
        var settings = new SettingsStore(dataDirectory).Load();
        if (ArgumentValue(args, "--theme") is { } theme) { settings.Theme = theme; settings.Normalize(); }
        using var controller = new AppController(dataDirectory, themes, settings, capturing);
        RegisteredWaitHandle? registration = null;
        if (showSignal is not null)
            registration = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => application.Dispatcher.BeginInvoke(new Action(() => controller.ShowPanel())), null, Timeout.Infinite, false);
        application.DispatcherUnhandledException += (_, eventArgs) =>
        {
            if (capturing) { Console.Error.WriteLine(eventArgs.Exception); application.Shutdown(1); }
            else System.Windows.MessageBox.Show(L.F("程序遇到异常：{0}", eventArgs.Exception.Message), L.AppName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            eventArgs.Handled = true;
        };
        application.Startup += async (_, _) =>
        {
            try
            {
                MainWindow? panel = null;
                if (capturing || args.Contains("--show") || !settings.StartInTray) panel = controller.ShowPanel(ArgumentValue(args, "--page") ?? "devices");
                await controller.StartAsync();
                if (!capturing) return;
                if (widgetScreenshot is not null)
                {
                    settings.WidgetEnabled = true;
                    foreach (var device in controller.Devices.Where(x => x.HasBattery).OrderByDescending(x => x.IsConnected).Take(3))
                        controller.GetPreference(device.Id).Pinned = true;
                    controller.ApplySettings(false);
                }
                await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(700); // Let the short battery transition settle before capture.
                if (screenshotPath is not null && panel is not null) CaptureWindow(panel, screenshotPath);
                if (widgetScreenshot is not null && controller.Widget is { } widget) CaptureWindow(widget, widgetScreenshot);
                controller.Exit();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                if (capturing) application.Shutdown(1);
                else System.Windows.MessageBox.Show($"启动失败：{error.Message}", "PowerHub");
            }
        };
        try { return application.Run(); }
        finally { registration?.Unregister(null); }
    }

    private static async Task<int> ScanAsync(string[] args)
    {
        using var plugins = new PluginHost(PluginDirectory, new AppSettings());
        var result = await new BatteryMonitorService(plugins).ScanAsync(!args.Contains("--no-ble"));
        var json = JsonSerializer.Serialize(result, JsonOptions);
        var outputIndex = Array.IndexOf(args, "--output");
        if (outputIndex >= 0)
        {
            if (outputIndex + 1 >= args.Length) throw new ArgumentException("--output 后需要文件路径");
            var outputPath = Path.GetFullPath(args[outputIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, json);
            Console.WriteLine(outputPath);
        }
        else Console.WriteLine(json);
        return 0;
    }

    private static string FindWorkspaceRoot()
    {
        var executableDirectory = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "PowerHub", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory : AppContext.BaseDirectory;
        foreach (var start in new[] { executableDirectory, Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PowerHub.csproj"))) return directory.FullName;
            }
        }
        return executableDirectory;
    }

    private static string? ArgumentValue(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{key} 后需要一个值");
        return args[index + 1];
    }

    private static void CaptureWindow(System.Windows.Window window, string output)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void CheckSettingsAndReminders()
    {
        static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        var settings = new AppSettings { LowBatteryThreshold = 200, PollIntervalSeconds = 1, Theme = "LIGHT", WidgetLeft = double.NaN };
        settings.Normalize();
        Check(settings.LowBatteryThreshold == 100 && settings.PollIntervalSeconds == 30 && settings.Theme == "Light" && settings.WidgetLeft is null, "设置边界归一化失败");
        settings.LowBatteryThreshold = 20;
        settings.RepeatReminderMinutes = 60;
        var now = DateTimeOffset.UtcNow;
        var low = new DeviceStatus { Id = "mouse", Name = "Mouse", BatteryPercent = 15, IsConnected = true, ConnectionKnown = true, UpdatedAt = now };
        var policy = new ReminderPolicy();
        IReadOnlyList<DeviceStatus> Evaluate(DeviceStatus device, int minutes)
        {
            var clock = now.AddMinutes(minutes);
            var reading = device with { UpdatedAt = clock };
            return policy.Evaluate(new ScanResult([reading], [], clock), clock.AddSeconds(-1), settings, clock);
        }
        Check(Evaluate(low, 0).Count == 1 && Evaluate(low, 1).Count == 0 && Evaluate(low, 60).Count == 1, "提醒冷却失败");
        Check(Evaluate(low with { IsCharging = true }, 61).Count == 0 && Evaluate(low, 62).Count == 1, "充电抑制或恢复重置失败");
        Check(Evaluate(low with { IsConnected = false }, 122).Count == 0 && Evaluate(low with { ConnectionKnown = false }, 122).Count == 0, "离线设备错误提醒");
        Check(policy.Evaluate(new ScanResult([low], [], now), now.AddSeconds(1), settings, now.AddMinutes(200)).Count == 0, "旧缓存错误提醒");
        settings.Devices[low.Id] = new DevicePreference { LowBatteryThreshold = 40, Pinned = true, DisplayName = "Mouse" };
        Check(Evaluate(low with { BatteryPercent = 30 }, 123).Count == 1, "单设备阈值未覆盖全局");
        settings.Devices[low.Id].NotificationsEnabled = false;
        Check(Evaluate(low, 200).Count == 0, "设备通知开关失败");
        settings.Devices[low.Id].NotificationsEnabled = true;
        settings.NotificationsEnabled = false;
        Check(Evaluate(low, 200).Count == 0 && new DeviceCard(low, settings).AlertsEnabled, "全局开关污染设备偏好");

        var temporary = Path.Combine(FindWorkspaceRoot(), ".tools", "settings-check-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(temporary);
        try
        {
            store.Save(settings);
            var restored = store.Load();
            Check(restored.Theme == "Light" && restored.GetThreshold("MOUSE") == 40 && restored.IsPinned("mouse"), "设置持久化失败");
            File.WriteAllText(store.FilePath, "{invalid json");
            Check(store.Load().LowBatteryThreshold == 20 && store.LastError is not null, "损坏设置未安全回退");
        }
        finally
        {
            if (File.Exists(store.FilePath)) File.Delete(store.FilePath);
            if (Directory.Exists(temporary)) Directory.Delete(temporary);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
