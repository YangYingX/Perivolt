using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace PowerHub.Services;

internal static class UpdateInstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{F8421AA2-85D8-4E06-B227-BDBD43B58BC9}_is1";

    internal static string? FindInstalledDirectory()
    {
        if (!Program.IsAppHostExecutable(Environment.ProcessPath)) return null;
        var currentDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var key = root.OpenSubKey(UninstallKey);
                if (MatchInstalledDirectory(currentDirectory, key?.GetValue("InstallLocation") as string) is { } directory)
                    return directory;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return null;
    }

    internal static string? MatchInstalledDirectory(string currentDirectory, string? recordedDirectory)
    {
        if (recordedDirectory is null || !Path.IsPathFullyQualified(recordedDirectory)) return null;
        try
        {
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDirectory));
            var recorded = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordedDirectory));
            return current.Equals(recorded, StringComparison.OrdinalIgnoreCase) ? current : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    internal static ProcessStartInfo CreateStartInfo(string installerPath, string? installDirectory, string dataDirectory)
    {
        if (!Path.IsPathFullyQualified(installerPath) || !installerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid update installer path.");
        var path = Path.GetFullPath(installerPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Update installer is unavailable.", path);
        var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        if (installDirectory is not null) start.ArgumentList.Add("/DIR=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory)));
        start.ArgumentList.Add("/PERIVOLT_DATA_DIR=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)));
        return start;
    }

    internal static void Start(string installerPath, string? installDirectory, string dataDirectory)
    {
        using var process = Process.Start(CreateStartInfo(installerPath, installDirectory, dataDirectory));
        if (process is null) throw new Win32Exception("Update installer did not start.");
    }
}
