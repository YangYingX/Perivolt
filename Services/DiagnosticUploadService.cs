using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PowerHub.Models;

namespace PowerHub.Services;

public sealed record DiagnosticUploadResult(Uri IssueUri, bool Duplicate);

public sealed class DiagnosticUploadService : IDisposable
{
    private readonly Uri? endpoint;
    private readonly string statePath;
    private readonly Func<HttpClient> createClient;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, DateTimeOffset> sent = new(StringComparer.Ordinal);
    private CancellationTokenSource? request;
    private bool autoRequest;
    private bool enabled;
    private bool disposed;
    private string? observedFingerprint;
    private int failedScans;
    private DateTimeOffset retryAfter;
    private string statusKey = "自动上报已关闭，可手动预览并发送";
    public bool IsConfigured => endpoint is not null;
    public bool IsUploading => request is not null;
    public string Status => L.T(IsConfigured ? statusKey : "诊断接收服务尚未配置");
    public Uri? LastIssueUri { get; private set; }
    public event Action? Changed;

    public DiagnosticUploadService(string dataDirectory) : this(ReadEndpoint(),
        Path.Combine(dataDirectory, "diagnostic-upload-state.json"),
        () => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 }, TimeProvider.System) { }

    internal DiagnosticUploadService(Uri? endpoint, string statePath, Func<HttpClient> createClient, TimeProvider clock)
    {
        this.endpoint = endpoint;
        this.statePath = statePath;
        this.createClient = createClient;
        this.clock = clock;
        LoadState();
    }

    public void Configure(bool autoReport)
    {
        if (disposed || enabled == (autoReport && IsConfigured)) return;
        enabled = autoReport && IsConfigured;
        observedFingerprint = null;
        failedScans = 0;
        if (!enabled && autoRequest) request?.Cancel();
        statusKey = enabled ? "自动上报已开启，连续读取失败时发送脱敏报告" : "自动上报已关闭，可手动预览并发送";
        Changed?.Invoke();
    }

    public void ObserveScan(ScanResult scan)
    {
        if (disposed || !enabled) return;
        var report = DiagnosticReportBuilder.Build(scan, UpdateService.DisplayVersion);
        if (report.FailedDeviceCount == 0)
        {
            observedFingerprint = null;
            failedScans = 0;
            return;
        }
        failedScans = observedFingerprint == report.Fingerprint ? Math.Min(3, failedScans + 1) : 1;
        observedFingerprint = report.Fingerprint;
        if (failedScans < 3 || IsUploading || clock.GetUtcNow() < retryAfter) return;
        if (sent.TryGetValue(report.Fingerprint, out var lastSent) && clock.GetUtcNow() - lastSent < TimeSpan.FromHours(24)) return;
        _ = SendAsync(report, true, default);
    }

    public Task<DiagnosticUploadResult?> UploadAsync(DiagnosticReport report, CancellationToken cancellationToken = default) =>
        SendAsync(report, false, cancellationToken);

    private async Task<DiagnosticUploadResult?> SendAsync(DiagnosticReport report, bool automatic, CancellationToken cancellationToken)
    {
        if (disposed || !IsConfigured || IsUploading || automatic && !enabled) return null;
        if (Encoding.UTF8.GetByteCount(report.Json) > 50 * 1024) return null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        request = cancellation;
        autoRequest = automatic;
        LastIssueUri = null;
        statusKey = "正在发送脱敏诊断报告…";
        Changed?.Invoke();
        try
        {
            // Constructing the service or collecting a report never opens a network connection.
            using var client = createClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Perivolt/{UpdateService.CurrentVersion}");
            using var content = new StringContent(report.Json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, cancellation.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                statusKey = "诊断上报暂时繁忙，请稍后重试";
                return null;
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("issueUrl", out var url)
                || url.ValueKind != JsonValueKind.String || !TryIssueUri(url.GetString(), out var issueUri)
                || !root.TryGetProperty("duplicate", out var duplicate) || duplicate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Invalid diagnostic response.");
            cancellation.Token.ThrowIfCancellationRequested();
            LastIssueUri = issueUri;
            statusKey = duplicate.GetBoolean() ? "已合并到已有 GitHub 问题" : "诊断报告已提交，可查看 GitHub 问题";
            sent[report.Fingerprint] = clock.GetUtcNow();
            SaveState();
            return new DiagnosticUploadResult(issueUri!, duplicate.GetBoolean());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!disposed) statusKey = automatic && !enabled ? "自动上报已关闭，可手动预览并发送" : "已取消诊断上报";
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException)
        {
            statusKey = "诊断上报未完成，请稍后手动重试";
            return null;
        }
        finally
        {
            retryAfter = clock.GetUtcNow().AddMinutes(5);
            request = null;
            autoRequest = false;
            if (!disposed) Changed?.Invoke();
        }
    }

    internal static bool TryIssueUri(string? value, out Uri? uri)
    {
        uri = null;
        const string prefix = "https://github.com/YangYingX/Perivolt/issues/";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var number = value[prefix.Length..];
        return number.Length is > 0 and <= 12 && number[0] != '0' && number.All(char.IsAsciiDigit)
            && Uri.TryCreate(value, UriKind.Absolute, out uri);
    }

    private static Uri? ReadEndpoint()
    {
        var value = typeof(DiagnosticUploadService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DiagnosticUploadEndpoint")?.Value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) ? uri : null;
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(statePath) || new FileInfo(statePath).Length > 64 * 1024) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(statePath));
            if (saved is null) return;
            foreach (var (key, time) in saved.Where(pair => pair.Key.Length == 64 && pair.Key.All(char.IsAsciiHexDigit)
                && pair.Value <= clock.GetUtcNow() && clock.GetUtcNow() - pair.Value < TimeSpan.FromHours(24)).Take(64)) sent[key] = time;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveState()
    {
        try
        {
            var recent = sent.Where(pair => clock.GetUtcNow() - pair.Value < TimeSpan.FromHours(24))
                .OrderByDescending(pair => pair.Value).Take(64).ToArray();
            sent.Clear();
            foreach (var pair in recent) sent[pair.Key] = pair.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath, JsonSerializer.Serialize(sent));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
