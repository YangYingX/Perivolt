using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace PowerHub.Mac.Services;

internal static class MacPlatform
{
    private const string StartupLabel = "io.github.yangyingx.perivolt";

    public static async Task NotifyAsync(string title, string message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireMacOS();
        var start = new ProcessStartInfo("/usr/bin/osascript")
        { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-");
        start.ArgumentList.Add(title);
        start.ArgumentList.Add(message);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = Process.Start(start) ?? throw new IOException("无法启动本地通知。");
        var errors = ReadErrorsAsync(process.StandardError, timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync("on run argv\ndisplay notification (item 2 of argv) with title (item 1 of argv)\nend run\n".AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var errorText = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"本地通知未完成：{errorText.Trim()}");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            timeout.Cancel();
            try { await errors.ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException) { }
        }
    }

    public static bool IsStartupEnabled()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var path = StartupPath();
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return false;
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 });
            var dictionary = XDocument.Load(reader).Root?.Element("dict");
            if (dictionary is null) return false;
            var elements = dictionary.Elements().ToArray();
            var fields = new Dictionary<string, XElement>(StringComparer.Ordinal);
            for (var index = 0; index + 1 < elements.Length; index++)
                if (elements[index].Name.LocalName == "key") fields[elements[index].Value] = elements[++index];
            var arguments = fields.GetValueOrDefault("ProgramArguments")?.Elements("string").Select(element => element.Value).ToArray();
            return fields.GetValueOrDefault("Label")?.Value == StartupLabel && fields.GetValueOrDefault("RunAtLoad")?.Name.LocalName == "true"
                && arguments is { Length: 3 } && arguments[1] == "--data-dir"
                && SamePath(arguments[0], Environment.ProcessPath) && SamePath(arguments[2], Program.DataDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)
        { return false; }
    }

    public static void SetStartup(bool enabled)
    {
        RequireMacOS();
        var path = StartupPath();
        if (!enabled) { File.Delete(path); return; }
        var executable = Environment.ProcessPath ?? throw new IOException("Application path is unavailable.");
        var macOsDirectory = Path.GetDirectoryName(executable);
        var contentsDirectory = Path.GetDirectoryName(macOsDirectory);
        var bundleDirectory = Path.GetDirectoryName(contentsDirectory);
        if (!Path.GetFileName(executable).Equals("Perivolt", StringComparison.Ordinal) || !File.Exists(executable)
            || Path.GetFileName(macOsDirectory) != "MacOS" || Path.GetFileName(contentsDirectory) != "Contents"
            || bundleDirectory?.EndsWith(".app", StringComparison.OrdinalIgnoreCase) != true)
            throw new IOException("请先安装 Perivolt.app，再开启登录启动。");
        var plist = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "Label"), new XElement("string", StartupLabel),
                new XElement("key", "ProgramArguments"), new XElement("array", new XElement("string", executable), new XElement("string", "--data-dir"), new XElement("string", Program.DataDirectory)),
                new XElement("key", "RunAtLoad"), new XElement("true"),
                new XElement("key", "LimitLoadToSessionType"), new XElement("string", "Aqua"))));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { plist.Save(temporary); File.Move(temporary, path, true); }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Open(string pathOrUrl)
    {
        RequireMacOS();
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrUrl);
        if (!(Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            pathOrUrl = Path.GetFullPath(pathOrUrl);
            if (!File.Exists(pathOrUrl) && !Directory.Exists(pathOrUrl)) throw new IOException("目标文件或目录不存在。");
        }
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        start.ArgumentList.Add(pathOrUrl);
        using var process = Process.Start(start);
    }

    private static string StartupPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", StartupLabel + ".plist");
    private static bool SamePath(string left, string? right) => !string.IsNullOrWhiteSpace(right)
        && Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.Ordinal);
    private static void RequireMacOS()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("此操作需要在 macOS 上运行。");
    }
    private static async Task<string> ReadErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var output = new System.Text.StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return output.ToString();
            if (output.Length + count > 64 * 1024) throw new IOException("本地通知错误输出过大。");
            output.Append(buffer, 0, count);
        }
    }
}
