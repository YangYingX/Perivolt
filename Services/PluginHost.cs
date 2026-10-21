using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PowerHub.Models;
using PowerHub.Plugins;

namespace PowerHub.Services;

public sealed class PluginHost : IDisposable
{
    private readonly object gate = new();
    private readonly AppSettings settings;
    private Dictionary<string, PluginState> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PluginRuntime> pendingInteractiveDisposals = [];
    private string? interactiveDataDirectory;
    private Dispatcher? interactiveDispatcher;
    private Action<string?>? selectTaskbarDevice;
    private bool disposed;
    public event Action? InteractiveChanged;
    public string DirectoryPath { get; }
    public IReadOnlyList<PluginEntry> Entries { get { lock (gate) return states.Values.Select(state => state.Entry).ToArray(); } }
    public bool HasInteractivePlugins { get { lock (gate) return !disposed && states.Values.Any(state => state.Entry.IsEnabled && !state.Faulted && state.Manifest.Kind == "interactive"); } }
    public IReadOnlyList<PluginThemeEntry> AvailableThemes
    {
        get
        {
            var themes = new List<PluginThemeEntry>();
            foreach (var state in Snapshot("theme"))
            {
                var runtime = Acquire(state);
                if (runtime is null) continue;
                try { themes.AddRange(ReadThemes(state, runtime)); }
                catch (Exception error) { Fault(state, error); }
                finally { Release(runtime); }
            }
            return themes;
        }
    }

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
                    if (manifest.Kind == "interactive" && !IsInteractiveId(manifest.Id)) continue;
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
                        Status = IsCompatible(manifest) ? "未启用" : "插件API版本不兼容"
                    };
                    if (manifest.Kind is not ("device" or "theme" or "utility" or "interactive")) entry.Status = "不支持的插件类型";
                    discovered.Add(entry.Id, new(manifest, directory, entry));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        bool interactiveChanged;
        lock (gate)
        {
            if (disposed) return;
            interactiveChanged = states.Values.Any(state => state.Manifest.Kind == "interactive");
            foreach (var state in states.Values.ToArray()) RetireLocked(state);
            states = discovered;
            foreach (var state in states.Values)
            {
                var builtin = state.Manifest.Id == "vendor-protocols" && state.Manifest.Assembly == "PowerHub.VendorProtocols.dll"
                    && state.Manifest.EntryType == "PowerHub.Extensions.VendorProtocolPlugin" && state.Manifest.Kind == "device";
                state.Entry.IsEnabled = IsCompatible(state.Manifest) &&
                    (settings.EnabledPlugins.TryGetValue(state.Entry.Id, out var enabled) ? enabled : builtin);
                if (state.Entry.IsEnabled) state.Entry.Status = "已启用，按需加载";
                UpdateOptionsLocked(state);
            }
        }
        if (interactiveChanged) NotifyInteractiveChanged();
    }

    public void SetEnabled(string id, bool enabled)
    {
        bool interactiveChanged;
        lock (gate)
        {
            if (disposed || !states.TryGetValue(id, out var state)) return;
            if (enabled && !IsCompatible(state.Manifest)) return;
            interactiveChanged = state.Manifest.Kind == "interactive";
            settings.EnabledPlugins[id] = enabled;
            state.Entry.IsEnabled = enabled;
            state.Faulted = false;
            RetireLocked(state);
            state.Entry.Status = enabled ? "已启用，按需加载" : "未启用";
        }
        if (interactiveChanged) NotifyInteractiveChanged();
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

    public void ConfigureInteractive(string dataDirectory, Dispatcher dispatcher, Action<string?> selectTaskbarDevice)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(selectTaskbarDevice);
        dispatcher.VerifyAccess();
        var directory = Path.GetFullPath(dataDirectory);
        lock (gate)
        {
            if (disposed) return;
            if (interactiveDispatcher is not null && (interactiveDispatcher != dispatcher ||
                !string.Equals(interactiveDataDirectory, directory, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("交互插件的 UI 线程和数据目录不能在运行中更改");
            if (interactiveDispatcher is null) dispatcher.ShutdownStarted += OnInteractiveDispatcherShutdown;
            interactiveDispatcher = dispatcher;
            interactiveDataDirectory = directory;
            this.selectTaskbarDevice = selectTaskbarDevice;
        }
    }

    public void UpdateInteractive(IReadOnlyList<DeviceStatus> devices, DateTimeOffset scannedAt)
    {
        if (!VerifyInteractiveAccess()) return;
        var entries = Snapshot("interactive");
        if (entries.Length == 0) return;
        var snapshot = devices.ToArray();
        foreach (var state in entries)
        {
            var runtime = AcquireInteractive(state);
            if (runtime is null) continue;
            try
            {
                IReadOnlyDictionary<string, string> options;
                lock (gate) options = state.Entry.Options.ToDictionary(option => option.Key, option => option.Value, StringComparer.OrdinalIgnoreCase);
                ((IInteractivePowerHubPlugin)runtime.Plugin).Update(new(snapshot, scannedAt, options));
            }
            catch (Exception error) { Fault(state, error); }
            finally { Release(runtime); }
        }
    }

    public IReadOnlyList<FrameworkElement> CreateTaskbarPopups(string? selectedDeviceId)
    {
        if (!VerifyInteractiveAccess()) return [];
        var entries = Snapshot("interactive");
        if (entries.Length == 0) return [];
        var result = new List<(PluginState State, PluginRuntime Runtime, FrameworkElement Popup)>();
        foreach (var state in entries)
        {
            var runtime = AcquireInteractive(state);
            if (runtime is null) continue;
            try
            {
                var popup = ((IInteractivePowerHubPlugin)runtime.Plugin).CreateTaskbarPopup(selectedDeviceId);
                if (popup is null) continue;
                if (popup.Dispatcher != interactiveDispatcher) throw new InvalidOperationException("插件弹层必须属于宿主 UI 线程");
                if (popup is Window || popup.Parent is not null) throw new InvalidOperationException("插件弹层必须是尚未挂载的界面元素");
                result.Add((state, runtime, popup));
            }
            catch (Exception error) { Fault(state, error); }
            finally { Release(runtime); }
        }
        // A callback may disable or reload another plugin while the popup list is being built.
        return result.Where(item => IsInteractiveActive(item.State, item.Runtime)).Select(item => item.Popup).ToArray();
    }

    public bool HandleTaskbarWheel(string? selectedDeviceId, int delta)
    {
        if (!VerifyInteractiveAccess()) return false;
        foreach (var state in Snapshot("interactive"))
        {
            var runtime = AcquireInteractive(state);
            if (runtime is null) continue;
            try
            {
                if (((IInteractivePowerHubPlugin)runtime.Plugin).HandleTaskbarWheel(selectedDeviceId, delta)) return true;
            }
            catch (Exception error) { Fault(state, error); }
            finally { Release(runtime); }
        }
        return false;
    }

    public bool OpenInteractive(string id)
    {
        if (!VerifyInteractiveAccess()) return false;
        var state = Snapshot("interactive").FirstOrDefault(state => state.Entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (state is null) return false;
        var runtime = AcquireInteractive(state);
        if (runtime is null) return false;
        try { ((IInteractivePowerHubPlugin)runtime.Plugin).Open(); return true; }
        catch (Exception error) { Fault(state, error); return false; }
        finally { Release(runtime); }
    }

    public async Task FlushInteractiveAsync(CancellationToken cancellationToken)
    {
        if (!VerifyInteractiveAccess()) return;
        foreach (var state in Snapshot("interactive"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginRuntime? runtime;
            lock (gate)
            {
                // Exit must not instantiate a plugin that has never been used.
                if (state.Runtime is not { Initialized: true }) continue;
                runtime = Acquire(state);
            }
            if (runtime is null) continue;
            try
            {
                await ((IInteractivePowerHubPlugin)runtime.Plugin).FlushAsync(cancellationToken).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { Fault(state, error); }
            finally { Release(runtime); }
        }
    }

    private bool VerifyInteractiveAccess()
    {
        if (disposed || interactiveDispatcher is null) return false;
        interactiveDispatcher.VerifyAccess();
        return true;
    }

    private bool IsInteractiveActive(PluginState state, PluginRuntime runtime)
    {
        lock (gate) return !disposed && state.Entry.IsEnabled && !state.Faulted && ReferenceEquals(state.Runtime, runtime);
    }

    private PluginRuntime? AcquireInteractive(PluginState state)
    {
        var runtime = Acquire(state);
        if (runtime is null) return null;
        if (runtime.Initializing) { Release(runtime); return null; }
        try
        {
            if (!runtime.Initialized)
            {
                runtime.Initializing = true;
                try
                {
                    var directory = Path.Combine(interactiveDataDirectory!, "plugins", state.Entry.Id);
                    foreach (var path in new[] { Path.GetDirectoryName(directory)!, directory })
                    {
                        try
                        {
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                                throw new InvalidDataException("插件数据目录不能使用目录链接");
                        }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                    }
                    Directory.CreateDirectory(directory);
                    ((IInteractivePowerHubPlugin)runtime.Plugin).Initialize(new(directory, selectedId =>
                    {
                        interactiveDispatcher!.VerifyAccess();
                        if (!IsInteractiveActive(state, runtime)) return;
                        selectTaskbarDevice!(selectedId);
                    }));
                    runtime.Initialized = true;
                }
                finally { runtime.Initializing = false; }
            }
            if (IsInteractiveActive(state, runtime)) return runtime;
        }
        catch (Exception error) { Fault(state, error); }
        Release(runtime);
        return null;
    }

    private void NotifyInteractiveChanged()
    {
        void Raise()
        {
            foreach (Action handler in InteractiveChanged?.GetInvocationList() ?? [])
                try { handler(); } catch (Exception) { }
        }
        var dispatcher = interactiveDispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Raise();
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Raise));
    }

    private void OnInteractiveDispatcherShutdown(object? sender, EventArgs args) => Dispose();

    public PluginTheme? ActiveTheme
    {
        get
        {
            if (!settings.Theme.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase)) return null;
            var selectedId = settings.Theme[7..];
            foreach (var state in Snapshot("theme"))
            {
                if (!state.Entry.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase) &&
                    !selectedId.StartsWith(state.Entry.Id + "/", StringComparison.OrdinalIgnoreCase)) continue;
                var runtime = Acquire(state);
                if (runtime is null) continue;
                try
                {
                    var selected = ReadThemes(state, runtime).FirstOrDefault(entry => entry.Value.Equals(settings.Theme, StringComparison.OrdinalIgnoreCase));
                    if (selected is not null) return selected.Theme;
                }
                catch (Exception error) { Fault(state, error); }
                finally { Release(runtime); }
            }
            return null;
        }
    }

    private static IReadOnlyList<PluginThemeEntry> ReadThemes(PluginState state, PluginRuntime runtime)
    {
        lock (runtime)
        {
            if (runtime.Themes is not null) return runtime.Themes;
            var result = new List<PluginThemeEntry>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var theme in runtime.Plugin.Themes.Take(501))
            {
                if (result.Count == 500 || string.IsNullOrWhiteSpace(theme.Id) || theme.Id.Length > 48 ||
                    theme.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') || !ids.Add(theme.Id))
                    throw new InvalidDataException("主题 ID 无效或重复，单个插件最多 500 套主题");
                foreach (var color in theme.Colors.Values)
                    if (!IsColor(color)) throw new InvalidDataException("主题颜色必须使用 #RRGGBB");
                var value = "plugin:" + state.Entry.Id + (theme.Id == state.Entry.Id ? "" : "/" + theme.Id);
                if (value.Length > 87) throw new InvalidDataException("主题 ID 过长");
                result.Add(new(value, string.IsNullOrWhiteSpace(theme.Name) ? state.Entry.Name : theme.Name, theme.Group, theme));
            }
            return runtime.Themes = result;
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
        PluginRuntime? runtime;
        bool interactiveFailure = false;
        lock (gate)
        {
            if (disposed || !state.Entry.IsEnabled || state.Faulted || !states.TryGetValue(state.Entry.Id, out var current) || !ReferenceEquals(current, state)) return null;
            var dispatcher = state.Manifest.Kind == "interactive" ? interactiveDispatcher : null;
            if (state.Manifest.Kind == "interactive" && (dispatcher is null || !dispatcher.CheckAccess())) return null;
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
                    if (state.Manifest.Kind == "interactive" && !typeof(IInteractivePowerHubPlugin).IsAssignableFrom(type))
                        throw new InvalidDataException("交互插件入口未实现IInteractivePowerHubPlugin");
                    var plugin = Activator.CreateInstance(type) as IPowerHubPlugin ?? throw new InvalidDataException("无法创建插件入口");
                    state.Runtime = new(context, plugin, dispatcher);
                    state.Entry.Status = "已加载";
                }
                catch (Exception error)
                {
                    context?.Unload();
                    state.Faulted = true;
                    state.Entry.Status = "加载失败：" + ErrorText(error);
                    interactiveFailure = state.Manifest.Kind == "interactive";
                }
            }
            runtime = state.Runtime;
            if (runtime is not null) runtime.Users++;
        }
        if (interactiveFailure) NotifyInteractiveChanged();
        return runtime;
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
        if (state.Manifest.Kind == "interactive") NotifyInteractiveChanged();
    }

    private void RetireLocked(PluginState state)
    {
        if (state.Runtime is not { } runtime) return;
        state.Runtime = null;
        runtime.Retired = true;
        QueueDisposalLocked(runtime);
    }

    private void QueueDisposalLocked(PluginRuntime runtime)
    {
        if (runtime.Dispatcher is not null) pendingInteractiveDisposals.Add(runtime);
        if (runtime.Users != 0 || runtime.DisposalQueued) return;
        runtime.DisposalQueued = true;
        if (runtime.Dispatcher is not { } dispatcher)
            ThreadPool.QueueUserWorkItem(_ => DisposeRuntime(runtime));
        else if (dispatcher.CheckAccess()) DisposeRuntime(runtime);
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => DisposeRuntime(runtime)));
    }

    private void DisposeRuntime(PluginRuntime runtime)
    {
        runtime.Dispatcher?.VerifyAccess();
        lock (gate)
        {
            if (runtime.Disposed || runtime.Users != 0) return;
            runtime.Disposed = true;
            pendingInteractiveDisposals.Remove(runtime);
        }
        try { runtime.Plugin.Dispose(); } catch (Exception) { }
        finally { try { runtime.Context.Unload(); } catch (Exception) { } }
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
    private static bool IsInteractiveId(string id) => id.Length > 0 && char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    private static bool IsCompatible(PluginManifest manifest) => manifest.Kind == "interactive"
        ? manifest.ApiVersion == 2 && IsInteractiveId(manifest.Id)
        : manifest.ApiVersion == 1 && manifest.Kind is "device" or "theme" or "utility";
    private static string ErrorText(Exception error) => (error is TargetInvocationException { InnerException: { } inner } ? inner.Message : error.Message).Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose()
    {
        var dispatcher = interactiveDispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            // Marshal before taking gate: plugin callbacks may be waiting for that same UI thread.
            dispatcher.Invoke(new Action(Dispose), DispatcherPriority.Send);
            return;
        }
        PluginRuntime[] pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var state in states.Values.ToArray()) RetireLocked(state);
            states.Clear();
            pending = pendingInteractiveDisposals.ToArray();
        }
        if (dispatcher?.CheckAccess() == true)
        {
            dispatcher.ShutdownStarted -= OnInteractiveDispatcherShutdown;
            foreach (var runtime in pending) DisposeRuntime(runtime);
        }
        NotifyInteractiveChanged();
    }

    private sealed class PluginState(PluginManifest manifest, string directory, PluginEntry entry)
    {
        internal PluginManifest Manifest { get; } = manifest;
        internal string Directory { get; } = directory;
        internal PluginEntry Entry { get; } = entry;
        internal PluginRuntime? Runtime;
        internal bool Faulted;
    }

    private sealed class PluginRuntime(PluginLoadContext context, IPowerHubPlugin plugin, Dispatcher? dispatcher = null)
    {
        internal PluginLoadContext Context { get; } = context;
        internal IPowerHubPlugin Plugin { get; } = plugin;
        internal Dispatcher? Dispatcher { get; } = dispatcher;
        internal int Users;
        internal bool Initializing;
        internal bool Initialized;
        internal bool Disposed;
        internal bool Retired;
        internal bool DisposalQueued;
        internal IReadOnlyList<PluginThemeEntry>? Themes;
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
