using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perivolt.Extensions.BatteryHistory;

internal sealed record HistorySample(DateTimeOffset At, int Percent, bool BreakBefore, bool SystemReported);
internal sealed record HistoryDevice(string IdHash, string Name, IReadOnlyList<HistorySample> Samples);
internal sealed record HistoryDocument(IReadOnlyList<HistoryDevice> Devices)
{
    public int Schema => 1;
}

internal static class HistoryStore
{
    internal const int MaxDevices = 16;
    internal const int MaxSamples = 12000;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    internal static readonly TimeSpan MaxLineGap = TimeSpan.FromMinutes(5);
    private const long MaxFileBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex AddressPattern = new(@"\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static async Task<FileStream> AcquireLeaseAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                if (waiting.Elapsed > TimeSpan.FromSeconds(10))
                    throw new IOException("等待上一实例完成保存超时；请停用后重新启用插件。", exception);
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    internal static string DisplayName(string? name)
    {
        var cleaned = AddressPattern.Replace(string.Concat((name ?? "").Where(c => !char.IsControl(c))), "").Trim();
        if (cleaned.Contains('\\') || cleaned.Contains("VID_", StringComparison.OrdinalIgnoreCase)) return "外设";
        return string.IsNullOrWhiteSpace(cleaned) ? "外设" : cleaned[..Math.Min(80, cleaned.Length)];
    }

    internal static async Task<HistoryDocument> LoadAsync(string path)
    {
        if (!File.Exists(path)) return new([]);
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("历史文件超过 32 MiB，未加载或覆盖。");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        using var json = await JsonDocument.ParseAsync(file, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1 ||
            !root.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("历史文件格式不受支持，未覆盖原文件。");
        var now = DateTimeOffset.UtcNow;
        var result = new List<HistoryDevice>();
        foreach (var item in devices.EnumerateArray().Take(MaxDevices))
        {
            var id = item.GetProperty("idHash").GetString()?.ToLowerInvariant() ?? "";
            if (!Regex.IsMatch(id, "^[0-9a-f]{64}$") || result.Any(d => d.IdHash == id)) continue;
            var name = DisplayName(item.GetProperty("name").GetString());
            var samples = new List<HistorySample>();
            foreach (var point in item.GetProperty("samples").EnumerateArray().Take(MaxSamples))
            {
                if (!point.GetProperty("at").TryGetDateTimeOffset(out var at) ||
                    !point.GetProperty("percent").TryGetInt32(out var percent) || percent is < 0 or > 100 ||
                    at < now - Retention || at > now) continue;
                samples.Add(new(at.ToUniversalTime(), percent, point.GetProperty("breakBefore").GetBoolean(),
                    point.GetProperty("systemReported").GetBoolean()));
            }
            var ordered = samples.OrderBy(s => s.At).DistinctBy(s => s.At).ToArray();
            if (ordered.Length > 0) result.Add(new(id, name, ordered));
        }
        return new(result);
    }

    internal static async Task SaveAsync(string path, HistoryDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, document, JsonOptions);
                await file.FlushAsync();
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
