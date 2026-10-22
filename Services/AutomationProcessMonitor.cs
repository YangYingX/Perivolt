using System.Diagnostics;
using PowerHub.Models;

namespace PowerHub.Services;

public static class AutomationProcessMonitor
{
    public static IReadOnlySet<string> Read(IEnumerable<string> names)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Select(AutomationRule.NormalizeProcess).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
                throw new ArgumentException(L.T("请输入应用进程名，例如 game.exe。"));
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) running.Add(name); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return running;
    }
}
