using System.IO;
using System.Runtime.InteropServices;

namespace PowerHub.Services;

internal static class StartupDiagnostics
{
    internal static string? Write(Exception error)
    {
        foreach (var folder in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerHub", "logs"),
                     Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? AppContext.BaseDirectory, "Perivolt-logs") })
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "startup.log");
                var entry = $"[{DateTimeOffset.Now:O}] v{UpdateService.DisplayVersion} | {RuntimeInformation.OSDescription} | {RuntimeInformation.ProcessArchitecture}\n{error}\n\n";
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.WriteAllText(path, entry);
                else File.AppendAllText(path, entry);
                return path;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    internal static void Report(Exception error)
    {
        var path = Write(error);
        var details = path is null ? L.T("未能写入启动日志。") : L.F("本地日志：{0}", path);
        System.Windows.MessageBox.Show(L.F("程序未能正常启动：{0}", error.Message) + "\n\n" + details,
            L.AppName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }
}
