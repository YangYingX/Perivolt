using System.IO;

namespace PowerHub.Services;

internal static class AppPaths
{
    internal static string ResolveDataDirectory(string? explicitDirectory, bool capturing, string workspaceRoot,
        string executableDirectory, string localApplicationData)
    {
        if (explicitDirectory is not null) return Path.GetFullPath(explicitDirectory);
        if (capturing) return Path.Combine(workspaceRoot, ".tools", "preview");
        if (string.IsNullOrWhiteSpace(localApplicationData))
            throw new IOException("无法取得当前用户的本地应用数据目录，请使用 --data-dir 指定设置目录。");

        var directory = Path.Combine(localApplicationData, "PowerHub");
        var target = Path.Combine(directory, "settings.json");
        if (File.Exists(target)) return directory;

        foreach (var legacyDirectory in new[] { Path.Combine(workspaceRoot, "artifacts", "data"),
                     Path.Combine(executableDirectory, "artifacts", "data"),
                     Path.Combine(localApplicationData, "Programs", "PowerHub", "artifacts", "data") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var source = Path.Combine(legacyDirectory, "settings.json");
            if (!File.Exists(source)) continue;
            var temporary = target + $".{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(directory);
                File.Copy(source, temporary, false);
                // Complete the copy before making it the active settings file; never replace existing preferences.
                File.Move(temporary, target, false);
                return directory;
            }
            catch (IOException) when (File.Exists(target)) { return directory; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"无法将原设置复制到新目录：{directory}。原文件已保留：{source}。" +
                    $"请检查目录权限，或使用 --data-dir 指定原设置目录。{error.Message}", error);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        return directory;
    }
}
