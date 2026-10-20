using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PowerHub.Native;

internal static class OverlayNative
{
    internal static void SetMousePassthrough(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var style = IntPtr.Size == 8 ? GetWindowLongPtrW(handle, -20).ToInt64() : GetWindowLongW(handle, -20);
        style |= 0x08000000 | 0x80; // NOACTIVATE | TOOLWINDOW; preserve WPF's layered window style.
        style = enabled ? style | 0x20 : style & ~0x20; // WS_EX_TRANSPARENT routes clicks to underlying windows.
        if (IntPtr.Size == 8) SetWindowLongPtrW(handle, -20, new IntPtr(style));
        else SetWindowLongW(handle, -20, (int)style);
    }

    internal static void MovePixels(Window window, int x, int y, int width, int height) =>
        SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, width, height, 0x0014);

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr handle, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
