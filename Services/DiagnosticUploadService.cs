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
    private const int MaximumResponseBytes = 8 * 1024;
    private const int MaximumLogBytes = 64 * 1024;
    private static readonly HashSet<string> WorkerErrors = new(StringComparer.Ordinal)
    {
        "invalid_json", "invalid_report", "report_too_large", "unsupported_encoding", "request_timeout",
        "client_ip_unavailable", "json_required", "method_not_allowed", "not_found", "not_configured",
        "rate_limited", "github_unavailable", "service_unavailable"
    };
    private static readonly HashSet<string> LogCodes = new(WorkerErrors, StringComparer.Ordinal)
    {
        "submitted", "duplicate", "network_error", "timeout", "cancelled", "invalid_response", "unknown_http_error", "unknown_error"
    };
    private readonly Uri? endpoint;
    private readonly string statePath;
    private readonly string logPath;
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
    private string? failureReason;
    private int? failureHttpStatus;
    public bool IsConfigured => endpoint is not null;
    public bool IsUploading => request is not null;
    public string Status => !IsConfigured ? L.T("诊断接收服务尚未配置") : failureReason is { } reason
        ? failureHttpStatus is { } status ? L.F("诊断上报未完成：{0}（HTTP {1}）", L.T(reason), status)
            : L.F("诊断上报未完成：{0}", L.T(reason)) : L.T(statusKey);
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
        logPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statePath))!, "logs", "diagnostic-upload.log");
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
        SetStatus(enabled ? "自动上报已开启，连续读取失败时发送脱敏报告" : "自动上报已关闭，可手动预览并发送");
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
        if (Encoding.UTF8.GetByteCount(report.Json) > 50 * 1024)
        {
            SetFailure(Reason("report_too_large"), null);
            WriteLog(automatic, "failure", null, "report_too_large");
            Changed?.Invoke();
            return null;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        request = cancellation;
        autoRequest = automatic;
        LastIssueUri = null;
        SetStatus("正在发送脱敏诊断报告…");
        Changed?.Invoke();
        int? responseStatus = null;
        try
        {
            // Constructing the service or collecting a report never opens a network connection.
            using var client = createClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(client.Timeout);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Perivolt/{UpdateService.CurrentVersion}");
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(report.Json, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            responseStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var code = await ReadWorkerErrorAsync(response, timeout.Token);
                SetFailure(Reason(code), responseStatus);
                WriteLog(automatic, "failure", responseStatus, code);
                return null;
            }
            using var document = JsonDocument.Parse(await ReadResponseAsync(response.Content, timeout.Token), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("issueUrl", out var url)
                || url.ValueKind != JsonValueKind.String || !TryIssueUri(url.GetString(), out var issueUri)
                || !root.TryGetProperty("duplicate", out var duplicate) || duplicate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Invalid diagnostic response.");
            timeout.Token.ThrowIfCancellationRequested();
            LastIssueUri = issueUri;
            SetStatus(duplicate.GetBoolean() ? "已合并到已有 GitHub 问题" : "诊断报告已提交，可查看 GitHub 问题");
            sent[report.Fingerprint] = clock.GetUtcNow();
            SaveState();
            WriteLog(automatic, "success", responseStatus, duplicate.GetBoolean() ? "duplicate" : "submitted");
            return new DiagnosticUploadResult(issueUri!, duplicate.GetBoolean());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!disposed) SetStatus(automatic && !enabled ? "自动上报已关闭，可手动预览并发送" : "已取消诊断上报");
            WriteLog(automatic, "cancelled", responseStatus, "cancelled");
            return null;
        }
        catch (OperationCanceledException)
        {
            SetFailure("诊断接收服务响应超时，请稍后重试", responseStatus);
            WriteLog(automatic, "failure", responseStatus, "timeout");
            return null;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            SetFailure("诊断接收服务返回无效响应，请稍后重试", responseStatus);
            WriteLog(automatic, "failure", responseStatus, "invalid_response");
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            SetFailure("无法连接诊断接收服务，请检查网络或代理", responseStatus);
            WriteLog(automatic, "failure", responseStatus, "network_error");
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

    private void SetStatus(string key) { statusKey = key; failureReason = null; failureHttpStatus = null; }
    private void SetFailure(string reason, int? status) { failureReason = reason; failureHttpStatus = status; }
    private static string Reason(string code) => code switch
    {
        "invalid_report" or "invalid_json" => "诊断报告未通过接收服务校验，请更新软件后重试",
        "report_too_large" => "诊断报告过大，无法发送",
        "rate_limited" => "诊断上报次数过多，请稍后重试",
        "not_configured" => "诊断接收服务尚未完成配置，请联系开发者",
        "github_unavailable" => "接收服务暂时无法提交到 GitHub，请稍后重试",
        "service_unavailable" => "诊断接收服务暂时不可用，请稍后重试",
        "request_timeout" => "诊断接收服务响应超时，请稍后重试",
        "client_ip_unavailable" => "接收服务无法确认连接来源，请稍后重试",
        "unsupported_encoding" or "json_required" or "method_not_allowed" or "not_found" => "诊断请求与接收服务不兼容，请更新软件后重试",
        _ => "诊断接收服务拒绝了请求，请稍后重试"
    };

    private static async Task<string> ReadWorkerErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = response.StatusCode == HttpStatusCode.TooManyRequests ? "rate_limited" : "unknown_http_error";
        try
        {
            using var document = JsonDocument.Parse(await ReadResponseAsync(response.Content, cancellationToken), new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String && error.GetString() is { } code && WorkerErrors.Contains(code) ? code : fallback;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return fallback; }
    }

    private static async Task<byte[]> ReadResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Diagnostic response is too large.");
        using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await source.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, MaximumResponseBytes + 1 - (int)buffer.Length)), cancellationToken);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("Diagnostic response is too large.");
            buffer.Write(bytes, 0, count);
        }
    }

    private void WriteLog(bool automatic, string outcome, int? status, string code)
    {
        try
        {
            // Persist only fixed categories and numbers; report contents, exception text,
            // device identifiers, fingerprints, credentials and URLs never reach this file.
            var entry = JsonSerializer.Serialize(new
            {
                at = clock.GetUtcNow().ToString("O"), mode = automatic ? "automatic" : "manual",
                outcome = outcome is "success" or "cancelled" ? outcome : "failure",
                httpStatus = status is >= 100 and <= 599 ? status : null,
                code = LogCodes.Contains(code) ? code : "unknown_error"
            }) + "\n";
            var bytes = Encoding.UTF8.GetBytes(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            using var file = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            if (file.Length + bytes.Length > MaximumLogBytes) file.SetLength(0);
            file.Seek(0, SeekOrigin.End);
            file.Write(bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException) { }
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
