using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using Accessibility;
using Point = System.Windows.Point;

namespace PowerHub.Native;

internal static class TaskbarNative
{
    internal readonly record struct Snapshot(IntPtr Handle, Rect Bounds, Rect MonitorBounds, Point ClientOrigin, uint Edge, DpiScale DpiScale);

    internal static bool TryGetTaskbar(out Snapshot snapshot)
    {
        snapshot = default;
        // Win32 geometry must use physical pixels even when the caller temporarily adopts Explorer's DPI context.
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previousContext == IntPtr.Zero) return false;
        try
        {
            var handle = FindWindow("Shell_TrayWnd", null);
            if (handle == IntPtr.Zero || !IsWindowVisible(handle) || !GetWindowRect(handle, out var nativeBounds)) return false;
            var monitor = MonitorFromWindow(handle, 0);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
            var bounds = ToRect(nativeBounds);
            var screen = ToRect(info.Monitor);
            if (bounds.IsEmpty || screen.IsEmpty || Rect.Intersect(bounds, screen).IsEmpty) return false;
            var origin = new NativePoint();
            if (!ClientToScreen(handle, ref origin)) return false;
            var appbar = new AppbarData { Size = Marshal.SizeOf<AppbarData>() };
            uint edge;
            if (SHAppBarMessage(5, ref appbar) != UIntPtr.Zero && appbar.Edge <= 3 &&
                !Rect.Intersect(ToRect(appbar.Bounds), screen).IsEmpty &&
                (appbar.Edge is 0 or 2) == (bounds.Height > bounds.Width))
                edge = appbar.Edge;
            else
            {
                // Explorer can briefly stop answering appbar queries while it rebuilds the taskbar.
                if (bounds.Height > bounds.Width)
                    edge = Math.Abs(bounds.Left - screen.Left) <= Math.Abs(bounds.Right - screen.Right) ? 0u : 2u;
                else
                    edge = Math.Abs(bounds.Top - screen.Top) <= Math.Abs(bounds.Bottom - screen.Bottom) ? 1u : 3u;
            }
            if (!IsFullyVisible(bounds, screen, edge)) return false;
            var scale = GetDpiForWindow(handle) / 96d;
            if (scale <= 0) return false;
            snapshot = new Snapshot(handle, bounds, screen, new Point(origin.X, origin.Y), edge, new DpiScale(scale, scale));
            return true;
        }
        finally
        {
            if (previousContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousContext);
        }
    }

    // UI Automation is synchronous: call only from a background MTA thread and allow at most one read in flight.
    // null means the shell did not expose enough information to safely place an embedded window.
    internal static Rect[]? ReadOccupiedBounds(IntPtr handle) => ReadOccupiedBounds(handle, null, true);

    internal static Rect[]? ReadOccupiedBounds(IntPtr handle, Func<Snapshot?>? readLayout, bool requireNotificationArea)
    {
        Snapshot? ReadLayout() => readLayout is not null ? readLayout() : TryGetTaskbar(out var snapshot) ? snapshot : null;
        if (ReadLayout() is not { } before || before.Handle != handle) return null;
        var occupied = new List<Rect>();
        var taskLists = new List<(IntPtr Handle, Rect Bounds, bool Leaf)>();
        var modern = false;
        var foundTray = false;
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previousContext == IntPtr.Zero) return null;
        try
        {
            EnumChildWindows(handle, (child, _) =>
            {
                if (!IsWindowVisible(child)) return true;
                GetWindowThreadProcessId(child, out var processId);
                if (processId == Environment.ProcessId || !GetWindowRect(child, out var rectangle)) return true;
                var classBuffer = new StringBuilder(256);
                if (GetClassName(child, classBuffer, classBuffer.Capacity) == 0) return true;
                var className = classBuffer.ToString();
                modern |= className == "Windows.UI.Composition.DesktopWindowContentBridge";
                var bounds = Rect.Intersect(ToRect(rectangle), before.Bounds);
                if (bounds.IsEmpty) return true;
                if (className is "MSTaskSwWClass" or "MSTaskListWClass") taskLists.Add((child, bounds, className == "MSTaskListWClass"));
                // WorkerW is the secondary taskbar's task-list container, like ReBarWindow32 on the primary.
                else if (className is not ("ReBarWindow32" or "WorkerW" or "Windows.UI.Composition.DesktopWindowContentBridge" or
                    "Windows.UI.Input.InputSite.WindowClass"))
                {
                    occupied.Add(bounds);
                    foundTray |= className == "TrayNotifyWnd";
                }
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            if (previousContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousContext);
        }
        var nativeOccupied = occupied.ToArray();
        var reliableButtons = false;
        try
        {
            var foundStart = false;
            var foundApplication = false;
            var count = 0;
            var stopwatch = Stopwatch.StartNew();
            var root = AutomationElement.FromHandle(handle);
            var walker = TreeWalker.ControlViewWalker;
            var cache = new CacheRequest();
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.ProcessIdProperty);

            bool ReadChildren(AutomationElement parent, bool inTaskList, int depth)
            {
                if (depth > 16) return false;
                for (var element = walker.GetFirstChild(parent, cache); element is not null; element = walker.GetNextSibling(element, cache))
                {
                    if (++count > 256 || stopwatch.ElapsedMilliseconds > 750) return false;
                    var current = element.Cached;
                    if (current.ProcessId == Environment.ProcessId || current.IsOffscreen) continue;
                    var className = current.ClassName;
                    var id = current.AutomationId;
                    var taskList = inTaskList || className is "MSTaskSwWClass" or "MSTaskListWClass" ||
                        className.StartsWith("Taskbar.TaskList", StringComparison.Ordinal);
                    var bounds = current.BoundingRectangle;
                    var visible = bounds.IsEmpty ? Rect.Empty : Rect.Intersect(bounds, before.Bounds);
                    var start = className == "Start" || id == "StartButton" || className == "Taskbar.StartButton";
                    var interactive = current.ControlType == ControlType.Button || current.ControlType == ControlType.ListItem ||
                        current.ControlType == ControlType.TabItem || current.ControlType == ControlType.MenuItem ||
                        current.ControlType == ControlType.Edit || current.ControlType == ControlType.ComboBox ||
                        current.ControlType == ControlType.Hyperlink || current.ControlType == ControlType.Slider;
                    var nativeRegion = className is "TrayNotifyWnd" or "TrayClockWClass" or "TrayShowDesktopButtonWClass" ||
                        className is "Chrome_WidgetWin_1";
                    if (!visible.IsEmpty && (interactive || start || nativeRegion))
                    {
                        occupied.Add(visible);
                        foundStart |= start;
                        foundApplication |= taskList && interactive && !start;
                    }
                    // A native notification area or third-party panel is already fully reserved.
                    if (!nativeRegion && !ReadChildren(element, taskList, depth + 1)) return false;
                }
                return true;
            }

            reliableButtons = ReadChildren(root, false, 0) && foundStart && foundApplication;
        }
        catch (Exception error) when (error is ElementNotAvailableException or ElementNotEnabledException or
            InvalidOperationException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            reliableButtons = false;
        }
        if (!reliableButtons)
        {
            if (requireNotificationArea && !foundTray || taskLists.Count == 0) return null;
            occupied = nativeOccupied.ToList();
            // Prefer the actual legacy list over its enclosing switcher, so a successful button read
            // is not masked by a second reservation of the enclosing full-width container.
            var lists = taskLists.Any(list => list.Leaf) ? taskLists.Where(list => list.Leaf) : taskLists;
            foreach (var taskList in lists)
            {
                if (!modern && ReadLegacyButtons(taskList.Handle, taskList.Bounds) is { } buttons)
                {
                    occupied.AddRange(buttons);
                    continue;
                }
                var reserved = taskList.Bounds;
                // Windows 11 can omit XAML buttons from UIA. Its native task switcher is the compact app group;
                // reserve adjacent Start/search/task-view areas on both sides, including RTL layouts.
                // A full-width legacy list stays reserved.
                if (modern)
                {
                    if (before.Edge is 1 or 3)
                        reserved = new Rect(taskList.Bounds.Left - 400 * before.DpiScale.DpiScaleX, taskList.Bounds.Top,
                            taskList.Bounds.Width + 800 * before.DpiScale.DpiScaleX, taskList.Bounds.Height);
                    else
                        reserved = new Rect(taskList.Bounds.Left, taskList.Bounds.Top - 400 * before.DpiScale.DpiScaleY,
                            taskList.Bounds.Width, taskList.Bounds.Height + 800 * before.DpiScale.DpiScaleY);
                }
                occupied.Add(Rect.Intersect(reserved, before.Bounds));
            }
            if (modern)
            {
                // Widgets can render inside the full-width composition host without their own accessible HWND.
                // Reserve both ends so right-to-left layouts are covered too.
                var bar = before.Bounds;
                if (before.Edge is 1 or 3)
                {
                    var width = Math.Min(176 * before.DpiScale.DpiScaleX, bar.Width);
                    occupied.Add(new Rect(bar.Left, bar.Top, width, bar.Height));
                    occupied.Add(new Rect(bar.Right - width, bar.Top, width, bar.Height));
                }
                else
                {
                    var height = Math.Min(176 * before.DpiScale.DpiScaleY, bar.Height);
                    occupied.Add(new Rect(bar.Left, bar.Top, bar.Width, height));
                    occupied.Add(new Rect(bar.Left, bar.Bottom - height, bar.Width, height));
                }
            }
        }
        if (ReadLayout() is not { } after || before != after) return null;
        return occupied.Where(rectangle => !rectangle.IsEmpty).Distinct().ToArray();
    }

    internal static bool IsFullyVisible(Rect bounds, Rect screen, uint edge)
    {
        if (bounds.IsEmpty || screen.IsEmpty || edge > 3) return false;
        var visible = Rect.Intersect(bounds, screen);
        if (visible.IsEmpty) return false;
        // An auto-hiding taskbar can remain visible to Win32 while it slides offscreen.
        // Allow a one-pixel border, but do not attach to a partially exposed bar.
        return edge is 0 or 2 ? visible.Width >= bounds.Width - 1 : visible.Height >= bounds.Height - 1;
    }

    private static Rect[]? ReadLegacyButtons(IntPtr handle, Rect limit)
    {
        IAccessible? root = null;
        var previousContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var interfaceId = typeof(IAccessible).GUID;
            if (AccessibleObjectFromWindow(handle, 0xFFFFFFFC, ref interfaceId, out root) < 0 || root is null) return null;
            var buttons = new List<Rect>();
            var foundButton = false;
            var visited = 0;
            var stopwatch = Stopwatch.StartNew();
            bool Read(IAccessible parent, int depth)
            {
                var count = parent.accChildCount;
                if (depth > 12 || count < 0 || count > 256 || visited + count > 256 || stopwatch.ElapsedMilliseconds > 750) return false;
                if (count == 0) return true;
                var children = new object[count];
                try
                {
                    if (AccessibleChildren(parent, 0, count, children, out var obtained) < 0 || obtained != count) return false;
                    foreach (var child in children)
                    {
                        if (++visited > 256 || stopwatch.ElapsedMilliseconds > 750) return false;
                        var accessible = child as IAccessible;
                        if (accessible is null && child is not int) return false;
                        var owner = accessible ?? parent;
                        var childId = accessible is null ? child : 0;
                        if (owner.get_accState(childId) is not int state || owner.get_accRole(childId) is not int role) return false;
                        if ((state & 0x18000) != 0) continue; // STATE_SYSTEM_INVISIBLE | STATE_SYSTEM_OFFSCREEN.
                        var button = role is 0x22 or 0x25 or 0x2B or 0x2C or 0x2D or 0x38 or 0x39 or 0x3A;
                        if (!button && accessible is not null && accessible.accChildCount > 0)
                        {
                            if (!Read(accessible, depth + 1)) return false;
                            continue;
                        }
                        // A container-only proxy does not prove where the task buttons end.
                        if (!button && role is 0x09 or 0x0A or 0x10 or 0x14 or 0x16 or 0x21) return false;
                        owner.accLocation(out var left, out var top, out var width, out var height, childId);
                        if (width <= 0 || height <= 0) continue;
                        var bounds = Rect.Intersect(new Rect(left, top, width, height), limit);
                        if (bounds.IsEmpty) continue;
                        buttons.Add(bounds);
                        foundButton |= button;
                    }
                    return true;
                }
                finally
                {
                    foreach (var child in children)
                        if (child is not null && Marshal.IsComObject(child)) Marshal.ReleaseComObject(child);
                }
            }
            return Read(root, 0) && foundButton ? buttons.Distinct().ToArray() : null;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (root is not null && Marshal.IsComObject(root)) Marshal.ReleaseComObject(root);
            if (previousContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousContext);
        }
    }

    private static Rect ToRect(NativeRect value) => value.Right <= value.Left || value.Bottom <= value.Top
        ? Rect.Empty : new Rect(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct AppbarData
    {
        public int Size;
        public IntPtr Handle;
        public uint CallbackMessage, Edge;
        public NativeRect Bounds;
        public IntPtr Parameter;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    private delegate bool EnumWindowCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder className, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppbarData data);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr handle, uint objectId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
    [DllImport("oleacc.dll")] private static extern int AccessibleChildren(IAccessible accessible, int start, int count,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);
}
