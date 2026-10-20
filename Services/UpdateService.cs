using PowerHub.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace PowerHub.Services;

public sealed class UpdateService : IDisposable
{
    private const string Endpoint = "https://api.github.com/repos/YangYingX/PowerHub/releases/latest";
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(12) };
    private CancellationTokenSource? request;
    private bool disposed;
    private string? notifiedVersion;
    public static string CurrentVersion => typeof(Program).Assembly.GetName().Version!.ToString(3);
    public bool IsEnabled { get; private set; }
    public bool IsChecking => request is not null;
    private string statusKey = "离线模式 · 更新检查已关闭";
    private object?[] statusArguments = [];
    public string Status => L.F(statusKey, statusArguments);
    private void SetStatus(string key, params object?[] arguments) { statusKey = key; statusArguments = arguments; }
    public Uri? ReleaseUri { get; private set; }
    public event Action? Changed;
    public event Action<string>? UpdateAvailable;

    public UpdateService() => timer.Tick += (_, _) => _ = CheckAsync();

    public void Configure(bool enabled)
    {
        if (disposed || IsEnabled == enabled) return;
        IsEnabled = enabled;
        if (enabled)
        {
            timer.Start();
            _ = CheckAsync();
        }
        else
        {
            timer.Stop();
            request?.Cancel();
            ReleaseUri = null;
            SetStatus("离线模式 · 更新检查已关闭");
        }
        Changed?.Invoke();
    }

    public async Task CheckAsync()
    {
        if (disposed || !IsEnabled || request is not null) return;
        using var cancellation = new CancellationTokenSource();
        request = cancellation;
        SetStatus("正在检查 GitHub 上的新版本…");
        Changed?.Invoke();
        try
        {
            // No client, DNS lookup or socket exists until the user enables this feature.
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 256 * 1024 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"PowerHub/{CurrentVersion}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.GetAsync(Endpoint, cancellation.Token);
            if (!IsEnabled || disposed || cancellation.IsCancellationRequested) return;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                SetStatus("暂时没有公开发行版");
                return;
            }
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(cancellation.Token);
            var release = ParseRelease(payload, Version.Parse(CurrentVersion));
            if (!IsEnabled || disposed || cancellation.IsCancellationRequested) return;
            ReleaseUri = release?.Uri;
            if (release is null) SetStatus("已是最新版本 · v{0}", CurrentVersion);
            else SetStatus("发现新版本 v{0}，可查看并下载安装", release.Value.Version);
            if (release is { } update && notifiedVersion != update.Version)
            {
                notifiedVersion = update.Version;
                UpdateAvailable?.Invoke(update.Version);
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            if (IsEnabled && !disposed)
                SetStatus("暂时无法检查更新，设备监测继续运行。可稍后重试。");
        }
        finally
        {
            request = null;
            if (!disposed) Changed?.Invoke();
            if (cancellation.IsCancellationRequested && IsEnabled && !disposed) _ = CheckAsync();
        }
    }

    internal static (string Version, Uri Uri)? ParseRelease(string payload, Version current)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Invalid release metadata.");
        var tag = tagValue.GetString()!;
        var number = tag.StartsWith('v') ? tag[1..] : tag;
        if (number.Split('.').Length != 3 || number.Any(c => c != '.' && (c < '0' || c > '9'))
            || !Version.TryParse(number, out var version))
            throw new InvalidDataException("Invalid release version.");
        if (version <= current) return null;
        // Construct a repository-local URL instead of trusting a URL from remote JSON.
        return (version.ToString(3), new Uri($"https://github.com/YangYingX/PowerHub/releases/tag/{Uri.EscapeDataString(tag)}"));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        request?.Cancel();
    }
}
