// Perivolt · 阳影X (YangYingX) · https://github.com/YangYingX/Perivolt
// Licensing and historical permissions: repository LICENSE and NOTICE.md.
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using PowerHub.Plugins;
using Point = System.Windows.Point;

namespace Perivolt.TaskbarTools;

internal sealed class NativeTaskbarTargets : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, int> _readOffset;
    private readonly Action<string, int> _saveOffset;
    private readonly Action _refreshPrimary;
    private readonly Action _onFailure;
    private readonly Dictionary<string, NativeTaskbarTarget> _secondary = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _eventGate = new();
    private readonly HashSet<NativeTaskbarTarget> _pendingTargets = [];
    private readonly List<IntPtr> _hooks = [];
    private Dictionary<IntPtr, NativeTaskbarTarget> _eventTargets = [];
    private WinEventCallback? _callback;
    private DispatcherOperation? _pendingOperation;
    private IntPtr _primaryHandle;
    private uint _processId;
    private bool _primaryPending;
    private bool _dispatchPending;
    private bool _disposed;

    public NativeTaskbarTargets(Dispatcher dispatcher, Func<string, int> readOffset,
        Action<string, int> saveOffset, Action refreshPrimary, Action onFailure)
    {
        _dispatcher = dispatcher;
        _readOffset = readOffset;
        _saveOffset = saveOffset;
        _refreshPrimary = refreshPrimary;
        _onFailure = onFailure;
        _callback = OnWinEvent;
    }

    public IReadOnlyDictionary<string, NativeTaskbarTarget> SecondaryTargets => _secondary;

    public bool Refresh()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return false;
        var primary = FindWindow("Shell_TrayWnd", null);
        var processId = WindowMatches(primary, 0, "Shell_TrayWnd") ? GetProcessId(primary) : 0;
        if (processId == 0) primary = IntPtr.Zero;
        var discovered = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if (processId != 0 && previousContext != IntPtr.Zero)
            {
                var primaryMonitor = ReadMonitor(primary)?.Device;
                EnumWindows((handle, _) =>
                {
                    if (!WindowMatches(handle, processId, "Shell_SecondaryTrayWnd") || ReadMonitor(handle) is not { } monitor ||
                        string.IsNullOrWhiteSpace(monitor.Device) || string.Equals(monitor.Device, primaryMonitor, StringComparison.OrdinalIgnoreCase)) return true;
                    if (ambiguous.Contains(monitor.Device)) return true;
                    if (!discovered.TryAdd(monitor.Device, handle))
                    {
                        discovered.Remove(monitor.Device);
                        ambiguous.Add(monitor.Device);
                    }
                    return true;
                }, IntPtr.Zero);
            }
        }
        finally
        {
            if (previousContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousContext);
        }

        var changed = primary != _primaryHandle || processId != _processId;
        var rebuildHooks = changed || _hooks.Count != (processId == 0 ? 0 : 3);
        foreach (var entry in _secondary.ToArray())
        {
            if (processId == _processId && discovered.TryGetValue(entry.Key, out var handle) && handle == entry.Value.Handle) continue;
            entry.Value.Dispose();
            _secondary.Remove(entry.Key);
            changed = true;
        }
        foreach (var entry in discovered)
        {
            if (_secondary.ContainsKey(entry.Key)) continue;
            _secondary.Add(entry.Key, new NativeTaskbarTarget(_dispatcher, entry.Key, entry.Value, processId,
                _readOffset(entry.Key), _saveOffset));
            changed = true;
        }
        lock (_eventGate)
        {
            _primaryHandle = primary;
            _processId = processId;
            _eventTargets = _secondary.Values.ToDictionary(target => target.Handle);
        }
        if (rebuildHooks) ReplaceHooks(processId);
        return changed;
    }

    private void ReplaceHooks(uint processId)
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        if (processId == 0 || _callback is null) return;
        foreach (var eventId in new uint[] { 0x8002, 0x8003, 0x800B }) // SHOW, HIDE, LOCATIONCHANGE.
        {
            var hook = SetWinEventHook(eventId, eventId, IntPtr.Zero, _callback, processId, 0, 2); // OUTOFCONTEXT | SKIPOWNPROCESS.
            if (hook != IntPtr.Zero) _hooks.Add(hook);
        }
        if (_hooks.Count == 3) return;
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear(); // The next topology refresh retries; the host's placement polling remains available.
    }

    private void OnWinEvent(IntPtr hook, uint eventId, IntPtr handle, int objectId, int childId, uint threadId, uint time)
    {
        if (handle == IntPtr.Zero || objectId != 0 || childId != 0) return;
        lock (_eventGate)
        {
            if (_disposed) return;
            if (handle == _primaryHandle) _primaryPending = true;
            else if (_eventTargets.TryGetValue(handle, out var target)) _pendingTargets.Add(target);
            else return;
            if (_dispatchPending) return;
            _dispatchPending = true;
            try { _pendingOperation = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(DispatchLayoutChanges)); }
            catch (Exception)
            {
                _dispatchPending = _primaryPending = false;
                _pendingTargets.Clear();
            }
        }
    }

    private void DispatchLayoutChanges()
    {
        NativeTaskbarTarget[] targets;
        bool primary;
        lock (_eventGate)
        {
            _pendingOperation = null;
            _dispatchPending = false;
            primary = _primaryPending;
            _primaryPending = false;
            targets = _pendingTargets.ToArray();
            _pendingTargets.Clear();
            if (_disposed) return;
        }
        try
        {
            if (primary) _refreshPrimary();
            foreach (var target in targets) target.NotifyLayoutChanged();
        }
        catch (Exception) { try { _onFailure(); } catch (Exception) { } }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        lock (_eventGate)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingOperation?.Abort();
            _pendingOperation = null;
            _dispatchPending = _primaryPending = false;
            _pendingTargets.Clear();
            _eventTargets.Clear();
            _primaryHandle = IntPtr.Zero;
        }
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        _callback = null;
        foreach (var target in _secondary.Values) target.Dispose();
        _secondary.Clear();
    }

    internal static PluginTaskbarLayout? ReadLayout(IntPtr handle, uint processId, string monitorName)
    {
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previousContext == IntPtr.Zero) return null;
        try
        {
            if (!WindowMatches(handle, processId, "Shell_SecondaryTrayWnd") || !IsWindowVisible(handle) ||
                !GetWindowRect(handle, out var nativeBounds) || ReadMonitor(handle) is not { } monitor ||
                !string.Equals(monitor.Device, monitorName, StringComparison.OrdinalIgnoreCase)) return null;
            var bounds = ToRect(nativeBounds);
            var screen = ToRect(monitor.Monitor);
            var dpi = GetDpiForWindow(handle);
            if (bounds.IsEmpty || screen.IsEmpty || dpi == 0 || bounds.Width == bounds.Height) return null;
            var scale = dpi / 96d;
            var vertical = bounds.Height > bounds.Width;
            var crossSize = vertical ? bounds.Width : bounds.Height;
            var screenCrossSize = vertical ? screen.Width : screen.Height;
            if (crossSize < 24 * scale || crossSize > screenCrossSize / 2) return null;
            uint edge;
            if (vertical)
            {
                if (bounds.Left < screen.Left - 1 || bounds.Right > screen.Right + 1 || bounds.Bottom <= screen.Top || bounds.Top >= screen.Bottom) return null;
                edge = Math.Abs(bounds.Left - screen.Left) <= Math.Abs(bounds.Right - screen.Right) ? 0u : 2u;
                if (Math.Abs(edge == 0 ? bounds.Left - screen.Left : bounds.Right - screen.Right) > 1) return null;
            }
            else
            {
                if (bounds.Top < screen.Top - 1 || bounds.Bottom > screen.Bottom + 1 || bounds.Right <= screen.Left || bounds.Left >= screen.Right) return null;
                edge = Math.Abs(bounds.Top - screen.Top) <= Math.Abs(bounds.Bottom - screen.Bottom) ? 1u : 3u;
                if (Math.Abs(edge == 1 ? bounds.Top - screen.Top : bounds.Bottom - screen.Bottom) > 1) return null;
            }
            var origin = new NativePoint();
            if (!ClientToScreen(handle, ref origin) || !WindowMatches(handle, processId, "Shell_SecondaryTrayWnd") || !IsWindowVisible(handle) ||
                !GetWindowRect(handle, out var after) || ToRect(after) != bounds || GetDpiForWindow(handle) != dpi ||
                ReadMonitor(handle) is not { } afterMonitor || !string.Equals(afterMonitor.Device, monitorName, StringComparison.OrdinalIgnoreCase) ||
                ToRect(afterMonitor.Monitor) != screen) return null;
            return new PluginTaskbarLayout(handle, bounds, screen, new Point(origin.X, origin.Y), edge, new DpiScale(scale, scale));
        }
        finally { SetThreadDpiAwarenessContext(previousContext); }
    }

    private static bool WindowMatches(IntPtr handle, uint processId, string className)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
        var actualProcessId = GetProcessId(handle);
        if (actualProcessId == 0 || processId != 0 && actualProcessId != processId) return false;
        var buffer = new StringBuilder(256);
        return GetClassName(handle, buffer, buffer.Capacity) != 0 && string.Equals(buffer.ToString(), className, StringComparison.Ordinal);
    }

    private static uint GetProcessId(IntPtr handle) { GetWindowThreadProcessId(handle, out var processId); return processId; }
    private static MonitorInfo? ReadMonitor(IntPtr handle)
    {
        var monitor = MonitorFromWindow(handle, 0);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info : null;
    }
    private static Rect ToRect(NativeRect value) => value.Right <= value.Left || value.Bottom <= value.Top
        ? Rect.Empty : new Rect(value.Left, value.Top, (double)value.Right - value.Left, (double)value.Bottom - value.Top);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool EnumWindowCallback(IntPtr handle, IntPtr parameter);
    private delegate void WinEventCallback(IntPtr hook, uint eventId, IntPtr handle, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder className, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}

internal sealed class NativeTaskbarTarget : IPluginTaskbarTarget, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly uint _processId;
    private Action<string, int>? _saveOffset;
    private int _disposed;

    internal NativeTaskbarTarget(Dispatcher dispatcher, string monitorName, IntPtr handle, uint processId,
        int initialOffset, Action<string, int> saveOffset)
    {
        _dispatcher = dispatcher;
        MonitorName = monitorName;
        Handle = handle;
        _processId = processId;
        InitialOffset = Math.Clamp(initialOffset, 0, 32767);
        _saveOffset = saveOffset;
    }

    public string MonitorName { get; }
    public IntPtr Handle { get; }
    public int InitialOffset { get; }
    public event Action? LayoutChanged;

    public PluginTaskbarLayout? ReadLayout()
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        var layout = NativeTaskbarTargets.ReadLayout(Handle, _processId, MonitorName);
        return Volatile.Read(ref _disposed) == 0 ? layout : null;
    }

    public void SaveOffset(int offset)
    {
        _dispatcher.VerifyAccess();
        if (Volatile.Read(ref _disposed) == 0) _saveOffset?.Invoke(MonitorName, Math.Clamp(offset, 0, 32767));
    }

    internal void NotifyLayoutChanged() { if (Volatile.Read(ref _disposed) == 0) LayoutChanged?.Invoke(); }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        Volatile.Write(ref _disposed, 1);
        LayoutChanged = null;
        _saveOffset = null;
    }
}
