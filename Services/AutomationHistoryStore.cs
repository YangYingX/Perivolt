using System.IO;
using System.Text.Json;
using PowerHub.Models;

namespace PowerHub.Services;

internal sealed class AutomationHistoryStore(string directory)
{
    private readonly string path = Path.Combine(directory, "automation-history.json");
    private readonly object gate = new();
    private AutomationLogEntry[]? pending;
    private Task writer = Task.CompletedTask;
    public string? Error { get; private set; }
    public event Action? ErrorChanged;

    public IReadOnlyList<AutomationLogEntry> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException(L.T("自动化记录文件过大。"));
            return (JsonSerializer.Deserialize<List<AutomationLogEntry>>(File.ReadAllText(path)) ?? [])
                .Where(entry => entry is not null && entry.RuleName is not null && entry.Steps is not null)
                .Take(200).ToArray();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { Error = L.T("自动化记录无法读取：") + error.Message; return []; }
    }

    public void Save(IReadOnlyList<AutomationLogEntry> entries)
    {
        lock (gate)
        {
            pending = entries.Take(200).ToArray();
            if (!writer.IsCompleted) return;
            writer = Task.Run(WritePending);
        }
    }

    private void WritePending()
    {
        while (true)
        {
            AutomationLogEntry[] snapshot;
            lock (gate)
            {
                if (pending is null) { writer = Task.CompletedTask; return; }
                snapshot = pending; pending = null;
            }
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
                File.Move(temporary, path, true);
                if (Error is not null) { Error = null; ErrorChanged?.Invoke(); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Error = L.T("自动化记录未保存：") + error.Message; ErrorChanged?.Invoke(); }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public Task FlushAsync(CancellationToken token)
    {
        lock (gate) return writer.WaitAsync(token);
    }
}
