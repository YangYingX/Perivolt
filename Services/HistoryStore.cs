using System.IO;
using System.Text.Json;
using PowerHub.Models;

namespace PowerHub.Services;

public sealed class HistoryStore(string dataDirectory)
{
    public async Task<string?> AppendAsync(ScanResult result)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var file = Path.Combine(dataDirectory, $"battery-{result.ScannedAt.ToLocalTime():yyyy-MM-dd}.jsonl");
            await File.AppendAllTextAsync(file, JsonSerializer.Serialize(result) + Environment.NewLine);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"历史记录保存失败：{exception.Message}";
        }
    }
}
