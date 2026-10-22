using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Perivolt.Extensions.PeripheralHub;

internal readonly record struct HubShortcut(uint Modifiers, uint VirtualKey, string Display)
{
    internal static bool TryParse(string text, out HubShortcut shortcut)
    {
        shortcut = default;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Length > 5 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 2u,
                "ALT" => 1u,
                "SHIFT" => 4u,
                _ => 0u
            };
            // Windows combinations and F12 are reserved by the OS/debugger.
            if (modifier == 0 || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        try
        {
            if (new KeyConverter().ConvertFromInvariantString(parts[^1]) is not Key key) return false;
            if (key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.F12 or
                Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or
                Key.LWin or Key.RWin or Key.Sleep) return false;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk is <= 0 or > 254) return false;
            var names = new List<string>();
            if ((modifiers & 2) != 0) names.Add("Ctrl");
            if ((modifiers & 1) != 0) names.Add("Alt");
            if ((modifiers & 4) != 0) names.Add("Shift");
            names.Add(key is >= Key.D0 and <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key.ToString());
            shortcut = new(modifiers, (uint)vk, string.Join("+", names));
            return true;
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException or FormatException)
        {
            return false;
        }
    }
}

internal sealed class HubHotkey : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action _pressed;
    private ushort _id;
    private bool _registered, _disposed;

    internal HubHotkey(Action pressed)
    {
        _pressed = pressed;
        _source = new HwndSource(new HwndSourceParameters("Perivolt.PeripheralHub.Hotkey")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: never creates a visible desktop surface.
            WindowStyle = 0, Width = 0, Height = 0
        });
        _source.AddHook(OnMessage);
        _id = GlobalAddAtom("Perivolt.PeripheralHub." + Guid.NewGuid().ToString("N"));
        if (_id == 0)
        {
            var error = Marshal.GetLastWin32Error();
            _source.Dispose();
            throw new Win32Exception(error);
        }
    }

    internal string Configure(string value)
    {
        if (_disposed) return "快捷键已停止";
        if (_registered) { UnregisterHotKey(_source.Handle, _id); _registered = false; }
        if (string.IsNullOrWhiteSpace(value)) return "快捷键已关闭；可从插件页打开";
        if (!HubShortcut.TryParse(value, out var shortcut))
            return "快捷键格式无效；请用 Ctrl+Alt+P 等组合（不支持 Win / F12）";
        _registered = RegisterHotKey(_source.Handle, _id, shortcut.Modifiers | 0x4000u, shortcut.VirtualKey);
        var error = _registered ? 0 : Marshal.GetLastWin32Error();
        return _registered ? shortcut.Display + " 呼出 / 收起 · Esc 关闭" :
            shortcut.Display + $" 无法注册（可能已被占用，Windows 错误 {error}）；可从插件页打开";
    }

    private IntPtr OnMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_disposed && _registered && message == 0x0312 && wParam.ToInt64() == _id)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered) { UnregisterHotKey(_source.Handle, _id); _registered = false; }
        _source.RemoveHook(OnMessage);
        _source.Dispose();
        if (_id != 0) { GlobalDeleteAtom(_id); _id = 0; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("kernel32.dll", EntryPoint = "GlobalAddAtomW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort GlobalAddAtom(string value);
    [DllImport("kernel32.dll")]
    private static extern ushort GlobalDeleteAtom(ushort atom);
}
