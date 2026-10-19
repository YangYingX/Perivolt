using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using PowerHub.Models;
using PowerHub.Plugins;

namespace PowerHub.Services;

public sealed class PluginHost : IDisposable
{
    private readonly object gate = new();
    private readonly AppSettings settings;
    private Dictionary<string, PluginState> states = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    public string DirectoryPath { get; }
    public IReadOnlyList<PluginEntry> Entries { get { lock (gate) return states.Values.Select(state => state.Entry).ToArray(); } }
    public IReadOnlyList<PluginEntry> AvailableThemes => Snapshot("theme").Select(state => state.Entry).ToArray();

    public PluginHost(string directory, AppSettings settings)
    {
        DirectoryPath = Path.GetFullPath(directory);
        this.settings = settings;
        Reload();
    }

    public void Reload()
    {
        var discovered = new Dictionary<string, PluginState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var directories = new[] { DirectoryPath }.Concat(Directory.EnumerateDirectories(DirectoryPath)
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0));
            foreach (var directory in directories.Order(StringComparer.OrdinalIgnoreCase))
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 65536) continue;
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 16 });
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 80) continue;
                    // The reserved built-in ID cannot be claimed by a manifest in another directory.
                    if (manifest.Id.Equals("vendor-protocols", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(Path.GetFullPath(file), Path.Combine(DirectoryPath, "vendor-protocols", "plugin.json"), StringComparison.OrdinalIgnoreCase)) continue;
                    if (discovered.ContainsKey(manifest.Id)) continue;
                    manifest.Options = (manifest.Options ?? []).Where(option => option is not null).ToList();
                    manifest.Vendors ??= [];
                    var entry = new PluginEntry
                    {
                        Id = manifest.Id, Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
                        Description = manifest.Description, Kind = manifest.Kind, Version = manifest.Version,
                        Status = manifest.ApiVersion == 1 ? "未启用" : "插件API版本不兼容"
                    };
                    if (manifest.Kind is not ("device" or "theme" or "utility")) entry.Status = "不支持的插件类型";
                    discovered.Add(entry.Id, new(manifest, directory, entry));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        lock (gate)
        {
            if (disposed) return;
            foreach (var state in states.Values) RetireLocked(state);
            states = discovered;
            foreach (var state in states.Values)
            {
                var builtin = state.Manifest.Id == "vendor-protocols" && state.Manifest.Assembly == "PowerHub.VendorProtocols.dll"
                    && state.Manifest.EntryType == "PowerHub.Extensions.VendorProtocolPlugin" && state.Manifest.Kind == "device";
                state.Entry.IsEnabled = state.Manifest.ApiVersion == 1 && (state.Manifest.Kind is "device" or "theme" or "utility") &&
                    (settings.EnabledPlugins.TryGetValue(state.Entry.Id, out var enabled) ? enabled : builtin);
                if (state.Entry.IsEnabled) state.Entry.Status = "已启用，按需加载";
                UpdateOptionsLocked(state);
            }
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (gate)
        {
            if (disposed || !states.TryGetValue(id, out var state)) return;
            if (enabled && (state.Manifest.ApiVersion != 1 || state.Manifest.Kind is not ("device" or "theme" or "utility"))) return;
            settings.EnabledPlugins[id] = enabled;
            state.Entry.IsEnabled = enabled;
            state.Faulted = false;
            RetireLocked(state);
            state.Entry.Status = enabled ? "已启用，按需加载" : "未启用";
        }
    }

    public void SetOption(string id, string key, string value)
    {
        lock (gate)
        {
            if (disposed || !states.TryGetValue(id, out var state) || !state.Manifest.Options.Any(option => option.Key == key)) return;
            if (!settings.PluginOptions.TryGetValue(id, out var options)) settings.PluginOptions[id] = options = new(StringComparer.OrdinalIgnoreCase);
            options[key] = (value ?? "")[..Math.Min(value?.Length ?? 0, 1024)];
            UpdateOptionsLocked(state);
        }
    }

    public PluginTheme? ActiveTheme
    {
        get
        {
            if (!settings.Theme.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase)) return null;
            var selectedId = settings.Theme[7..];
            foreach (var state in Snapshot("theme"))
            {
                if (!state.Entry.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) continue;
                var runtime = Acquire(state);
                if (runtime is null) continue;
                try
                {
                    if (!runtime.ThemeRead)
                    {
                        var theme = runtime.Plugin.Theme;
                        if (theme is not null)
                        {
                            foreach (var color in theme.Colors.Values)
                                if (!IsColor(color)) throw new InvalidDataException("主题颜色必须使用 #RRGGBB");
                        }
                        runtime.Theme = theme;
                        runtime.ThemeRead = true;
                    }
                    if (runtime.Theme is not null) return runtime.Theme;
                }
                catch (Exception error) { Fault(state, error); }
                finally { Release(runtime); }
            }
            return null;
        }
    }

    public PluginOutput Evaluate(IReadOnlyList<DeviceStatus> devices, DateTimeOffset now)
    {
        var suppress = false;
        var notifications = new List<PluginNotification>();
        foreach (var state in Snapshot("utility"))
        {
            var runtime = Acquire(state);
            if (runtime is null) continue;
            try
            {
                IReadOnlyDictionary<string, string> options;
                lock (gate) options = state.Entry.Options.ToDictionary(option => option.Key, option => option.Value, StringComparer.OrdinalIgnoreCase);
                var output = runtime.Plugin.OnScan(new(devices, now, options));
                if (output is null) continue;
                suppress |= output.SuppressLowBatteryAlerts;
                if (output.Notifications is not null)
                    notifications.AddRange(output.Notifications.Where(notification => notification is not null && !string.IsNullOrWhiteSpace(notification.Message)).Take(20));
            }
            catch (Exception error) { Fault(state, error); }
            finally { Release(runtime); }
        }
        return new(suppress, notifications);
    }

    public HidPluginResult? ReadHid(HidDeviceContext device)
    {
        foreach (var state in Snapshot("device"))
        {
            if (device.Device.VendorId is not int vendor || !state.Manifest.Vendors.Contains(vendor)) continue;
            var runtime = Acquire(state);
            if (runtime is null) continue;
            try
            {
                var result = runtime.Plugin.ReadHid(device);
                if (result is not null) return result;
            }
            catch (Exception error) { Fault(state, error); }
            finally { Release(runtime); }
        }
        return null;
    }

    private PluginState[] Snapshot(string kind)
    {
        lock (gate) return disposed ? [] : states.Values.Where(state => state.Entry.IsEnabled && state.Manifest.Kind == kind && !state.Faulted).ToArray();
    }

    private PluginRuntime? Acquire(PluginState state)
    {
        lock (gate)
        {
            if (disposed || !state.Entry.IsEnabled || state.Faulted || !states.TryGetValue(state.Entry.Id, out var current) || !ReferenceEquals(current, state)) return null;
            if (state.Runtime is null)
            {
                PluginLoadContext? context = null;
                try
                {
                    var file = AssemblyPath(state.Directory, state.Manifest.Assembly);
                    if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new FileNotFoundException("插件DLL不存在或使用了文件链接");
                    context = new(state.Directory);
                    var assembly = context.LoadFromAssemblyPath(file);
                    var type = assembly.GetType(state.Manifest.EntryType, throwOnError: true)!;
                    if (!typeof(IPowerHubPlugin).IsAssignableFrom(type)) throw new InvalidDataException("入口类型未实现IPowerHubPlugin");
                    var plugin = Activator.CreateInstance(type) as IPowerHubPlugin ?? throw new InvalidDataException("无法创建插件入口");
                    state.Runtime = new(context, plugin);
                    state.Entry.Status = "已加载";
                }
                catch (Exception error)
                {
                    context?.Unload();
                    state.Faulted = true;
                    state.Entry.Status = "加载失败：" + ErrorText(error);
                    return null;
                }
            }
            state.Runtime.Users++;
            return state.Runtime;
        }
    }

    private void Release(PluginRuntime runtime)
    {
        lock (gate)
        {
            runtime.Users--;
            if (runtime.Retired) QueueDisposalLocked(runtime);
        }
    }

    private void Fault(PluginState state, Exception error)
    {
        lock (gate)
        {
            state.Faulted = true;
            state.Entry.Status = "运行异常：" + ErrorText(error) + "；重新启用可重试";
            RetireLocked(state);
        }
    }

    private void RetireLocked(PluginState state)
    {
        if (state.Runtime is not { } runtime) return;
        state.Runtime = null;
        runtime.Retired = true;
        QueueDisposalLocked(runtime);
    }

    private static void QueueDisposalLocked(PluginRuntime runtime)
    {
        if (runtime.Users != 0 || runtime.DisposalQueued) return;
        runtime.DisposalQueued = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { runtime.Plugin.Dispose(); } catch (Exception) { }
            finally { try { runtime.Context.Unload(); } catch (Exception) { } }
        });
    }

    private void UpdateOptionsLocked(PluginState state)
    {
        settings.PluginOptions.TryGetValue(state.Entry.Id, out var saved);
        state.Entry.Options = state.Manifest.Options.Where(option => !string.IsNullOrWhiteSpace(option.Key))
            .DistinctBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
            .Select(option => new PluginOptionValue(option.Key, option.Label,
                saved is not null && saved.TryGetValue(option.Key, out var value) ? value : option.DefaultValue)).ToArray();
    }

    private static string AssemblyPath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || Path.GetFileName(name) != name || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件DLL必须位于清单同一目录");
        var file = Path.GetFullPath(Path.Combine(directory, name));
        if (!string.Equals(Path.GetDirectoryName(file), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件DLL必须位于清单同一目录");
        return file;
    }

    private static bool IsColor(string value) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    private static string ErrorText(Exception error) => (error is TargetInvocationException { InnerException: { } inner } ? inner.Message : error.Message).Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var state in states.Values) RetireLocked(state);
            states.Clear();
        }
    }

    private sealed class PluginState(PluginManifest manifest, string directory, PluginEntry entry)
    {
        internal PluginManifest Manifest { get; } = manifest;
        internal string Directory { get; } = directory;
        internal PluginEntry Entry { get; } = entry;
        internal PluginRuntime? Runtime;
        internal bool Faulted;
    }

    private sealed class PluginRuntime(PluginLoadContext context, IPowerHubPlugin plugin)
    {
        internal PluginLoadContext Context { get; } = context;
        internal IPowerHubPlugin Plugin { get; } = plugin;
        internal int Users;
        internal bool Retired;
        internal bool DisposalQueued;
        internal PluginTheme? Theme;
        internal bool ThemeRead;
    }

    private sealed class PluginLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        private static readonly HashSet<string> PlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        protected override Assembly? Load(AssemblyName name)
        {
            var contracts = typeof(IPowerHubPlugin).Assembly;
            if (name.Name == contracts.GetName().Name) return contracts;
            if (name.Name is null || PlatformAssemblies.Contains(name.Name)) return null;
            var file = AssemblyPath(directory, name.Name + ".dll");
            if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new FileNotFoundException("插件依赖必须位于插件目录", name.Name);
            return LoadFromAssemblyPath(file);
        }
    }
}
