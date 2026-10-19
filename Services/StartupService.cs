using Microsoft.Win32;
using System.IO;

namespace PowerHub.Services;

internal static class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("PowerHub") is string command && command.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!enabled) { key.DeleteValue("PowerHub", false); return; }
        var executable = Environment.ProcessPath;
        if (executable is null || !Path.GetFileName(executable).Equals("PowerHub.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从发布版 PowerHub.exe 开启开机启动。");
        key.SetValue("PowerHub", $"\"{executable}\" --startup", RegistryValueKind.String);
    }
}
