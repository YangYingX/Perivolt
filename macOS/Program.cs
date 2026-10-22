using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using PowerHub.Mac.Services;
using PowerHub.Models;
using PowerHub.Services;

namespace PowerHub.Mac;

internal static class Program
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string[] Arguments { get; private set; } = [];
    public static string DataDirectory { get; private set; } = "";
    private static FileStream? instanceLock;
    private static CancellationTokenSource? lifetime;

    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        try
        {
            var preview = args.Contains("--preview") || args.Contains("--screenshot");
            DataDirectory = Path.GetFullPath(ArgumentValue(args, "--data-dir") ?? (preview
                ? Path.Combine(Path.GetTempPath(), "Perivolt.Mac.Preview")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Perivolt")));
            if (args.Contains("--self-check")) return SelfCheck();
            if (!OperatingSystem.IsMacOS() && !preview)
            {
                Console.Error.WriteLine("Perivolt.Mac requires macOS. Use PowerHub.csproj for Windows.");
                return 1;
            }
            if (args.Contains("--scan"))
            {
                var scan = new MacBatteryProvider().ScanAsync(!args.Contains("--no-ble")).GetAwaiter().GetResult();
                var json = JsonSerializer.Serialize(scan, JsonOptions);
                if (ArgumentValue(args, "--output") is { } output)
                {
                    var outputPath = Path.GetFullPath(output);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    File.WriteAllText(outputPath, json);
                }
                else Console.WriteLine(json);
                return 0;
            }
            if (preview)
                return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
            Directory.CreateDirectory(DataDirectory);
            var pipeName = "Perivolt.Mac." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory)))[..24];
            try { instanceLock = new FileStream(Path.Combine(DataDirectory, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                client.Connect(3000);
                client.WriteByte(1);
                return 0;
            }
            lifetime = new();
            _ = ListenAsync(pipeName, lifetime.Token);
            return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.AppendAllText(Path.Combine(DataDirectory, "startup.log"), $"{DateTimeOffset.Now:O}\n{error}\n");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            return 1;
        }
        finally { lifetime?.Cancel(); lifetime?.Dispose(); instanceLock?.Dispose(); }
    }

    private static async Task ListenAsync(string name, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                var buffer = new byte[1];
                if (await server.ReadAsync(buffer, token) > 0)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => (Application.Current as App)?.Controller?.ShowMain());
            }
            catch (OperationCanceledException) { return; }
            catch (IOException)
            {
                try { await Task.Delay(250, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    internal static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int SelfCheck()
    {
        var parent = Path.GetFullPath(ArgumentValue(Arguments, "--data-dir") ?? Path.GetTempPath());
        var directory = Path.Combine(parent, "perivolt-mac-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings();
            settings.Normalize();
            settings.Devices["sample"] = new() { CustomName = "键盘", LowBatteryThreshold = 25, Pinned = true };
            var store = new SettingsStore(directory);
            store.Save(settings);
            var restored = store.Load();
            if (restored.GetThreshold("sample") != 25 || !restored.IsPinned("sample") || restored.GetDeviceName(new() { Id = "sample" }) != "键盘")
                throw new InvalidOperationException("Settings round trip failed.");
            var now = DateTimeOffset.Now;
            var device = new DeviceStatus { Id = "sample", IsConnected = true, BatteryPercent = 0, UpdatedAt = now };
            var policy = new ReminderPolicy();
            var scan = new ScanResult([device], [], now);
            if (policy.Evaluate(scan, now, restored, now).Count != 1 || policy.Evaluate(scan, now, restored, now).Count != 0)
                throw new InvalidOperationException("Low battery/repeat reminder failed.");
            if (new ReminderPolicy().Evaluate(scan with { Devices = [device with { IsCharging = true }] }, now, restored, now).Count != 0)
                throw new InvalidOperationException("Charging suppression failed.");
            L.SetLanguage("en");
            if (L.T("正在充电") == "正在充电") throw new InvalidOperationException("Locale resources missing.");
            Console.WriteLine("Perivolt macOS self-check passed: settings, zero battery, reminders, charging suppression, locale resources.");
            return 0;
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
