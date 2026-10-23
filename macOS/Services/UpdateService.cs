using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerHub.Services;

// The macOS updater shares the release channel, but never selects Windows installers.
public sealed class UpdateService : IDisposable
{
    private const string Endpoint = "https://api.github.com/repos/YangYingX/Perivolt/releases/latest";
    private const string DownloadPrefix = "https://github.com/YangYingX/Perivolt/releases/download/";
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private readonly string cacheDirectory;
    private readonly string currentDisplayVersion;
    private readonly Func<HttpClient> createClient;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private readonly Timer timer;
    private CancellationTokenSource? request;
    private ReleaseInfo? release;
    private bool disposed;
    private bool downloadCancelled;
    private bool pendingCheck;
    private string? notifiedVersion;
    private string status = "离线模式 · 更新检查已关闭";

    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version!.ToString(3);
    public static string DisplayVersion => typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0] ?? CurrentVersion;
    public bool IsEnabled { get; private set; }
    public bool IsChecking => request is not null && !IsDownloading;
    public bool IsDownloading { get; private set; }
    public bool IsBusy => request is not null;
    public int DownloadProgress { get; private set; }
    public bool CanDownloadInstaller => IsEnabled && release?.Package is not null;
    public string? AvailableVersion => release?.Version;
    public string Status => L.T(status);
    public Uri? ReleaseUri => release?.PageUri;
    public event Action? Changed;
    public event Action<string>? UpdateAvailable;

    public UpdateService(string? dataDirectory = null) : this(Path.Combine(dataDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Perivolt"), "updates"),
        DisplayVersion, () => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 256 * 1024 }) { }

    internal UpdateService(string cacheDirectory, string currentDisplayVersion, Func<HttpClient> createClient)
    {
        this.cacheDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
        this.currentDisplayVersion = currentDisplayVersion;
        this.createClient = createClient;
        timer = new Timer(_ => Dispatch(() => _ = CheckAsync()), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Configure(bool enabled)
    {
        if (disposed || IsEnabled == enabled) return;
        IsEnabled = enabled;
        if (enabled)
        {
            timer.Change(TimeSpan.FromHours(12), TimeSpan.FromHours(12));
            pendingCheck = IsBusy;
            _ = CheckAsync();
        }
        else
        {
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            pendingCheck = false;
            CancelRequest();
            release = null;
            status = "离线模式 · 更新检查已关闭";
        }
        Notify();
    }

    public async Task CheckAsync()
    {
        if (disposed || !IsEnabled) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (Interlocked.CompareExchange(ref request, cancellation, null) is not null) return;
        status = "正在检查 GitHub 上的新版本…";
        Notify();
        try
        {
            using var client = CreateClient();
            using var message = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            message.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(message, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                release = null;
                status = "暂时没有公开发行版";
                return;
            }
            response.EnsureSuccessStatusCode();
            var found = ParseRelease(await response.Content.ReadAsStringAsync(cancellation.Token), currentDisplayVersion);
            cancellation.Token.ThrowIfCancellationRequested();
            if (disposed || !IsEnabled) return;
            release = found;
            status = found is null ? $"已是最新版本 · v{currentDisplayVersion}"
                : found.Package is null ? $"发现 v{found.Version}，尚无适合本机且可校验的 macOS 安装包。"
                : $"发现新版本 v{found.Version}，可下载 macOS 安装包";
            if (found?.Package is not null && notifiedVersion != found.Version)
            {
                notifiedVersion = found.Version;
                Dispatch(() => UpdateAvailable?.Invoke(found.Version));
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            if (IsEnabled && !disposed) status = "暂时无法检查更新，设备监测继续运行。可稍后重试。";
        }
        finally { FinishRequest(cancellation); }
    }

    public async Task<string?> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (disposed || !IsEnabled || release?.Package is not { } package) return null;
        var checksum = release.Checksum;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(10));
        if (Interlocked.CompareExchange(ref request, cancellation, null) is not null) return null;
        IsDownloading = true;
        downloadCancelled = false;
        DownloadProgress = 0;
        string? attemptDirectory = null;
        var completed = false;
        try
        {
            status = "正在获取安装包校验信息…";
            Notify();
            using var client = CreateClient();
            var hash = package.Hash;
            if (hash is null && checksum is not null)
            {
                using var checksumResponse = await GetAssetAsync(client, checksum.Uri, cancellation.Token);
                var bytes = await ReadBytesAsync(checksumResponse, checksum.Size, 64 * 1024, cancellation.Token);
                if (checksum.Hash is not null && !HashMatches(bytes, checksum.Hash))
                    throw new InvalidDataException("Invalid checksum asset.");
                hash = ParseChecksum(new UTF8Encoding(false, true).GetString(bytes), package.Name, checksum.Name == package.Name + ".sha256");
            }
            if (hash is null) throw new InvalidDataException("Missing macOS package checksum.");
            cancellation.Token.ThrowIfCancellationRequested();
            attemptDirectory = Path.Combine(cacheDirectory, "download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attemptDirectory);
            var partialPath = Path.Combine(attemptDirectory, package.Name + ".part");
            using (var response = await GetAssetAsync(client, package.Uri, cancellation.Token))
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
                await CopyAssetAsync(response, output, package.Size, cancellation.Token);
            status = "正在校验 macOS 安装包…";
            Notify();
            if (!await VerifyFileAsync(partialPath, package.Size, hash, cancellation.Token))
                throw new InvalidDataException("macOS package verification failed.");
            cancellation.Token.ThrowIfCancellationRequested();
            if (disposed || !IsEnabled) return null;
            var readyPath = Path.Combine(attemptDirectory, package.Name);
            File.Move(partialPath, readyPath);
            completed = true;
            status = "安装包已校验。解压或打开后，将 Perivolt 拖入应用程序替换旧版。";
            if (OperatingSystem.IsMacOS())
            {
                // Reveal the verified download; replacing the running .app remains an explicit user action.
                try
                {
                    var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                    start.ArgumentList.Add("-R");
                    start.ArgumentList.Add(readyPath);
                    Process.Start(start)?.Dispose();
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                { status = $"安装包已校验，保存于：{readyPath}"; }
            }
            return readyPath;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or TimeoutException
            or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (IsEnabled && !disposed) status = downloadCancelled || cancellationToken.IsCancellationRequested
                ? "下载已取消，当前版本继续运行。" : "下载或校验未完成，当前版本继续运行。可重试或前往发行页下载。";
            return null;
        }
        finally
        {
            if (!completed && attemptDirectory is not null) DeleteAttempt(attemptDirectory);
            IsDownloading = false;
            FinishRequest(cancellation);
        }
    }

    public void CancelDownload()
    {
        if (!IsDownloading) return;
        downloadCancelled = true;
        CancelRequest();
    }

    private void CancelRequest()
    {
        try { request?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void FinishRequest(CancellationTokenSource cancellation)
    {
        Interlocked.CompareExchange(ref request, null, cancellation);
        if (!disposed) Notify();
        if (pendingCheck && IsEnabled && !disposed) { pendingCheck = false; _ = CheckAsync(); }
    }

    private void Dispatch(Action action)
    {
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context)) action();
        else context.Post(_ => { if (!disposed) action(); }, null);
    }

    private void Notify() => Dispatch(() => Changed?.Invoke());

    private HttpClient CreateClient()
    {
        var client = createClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Perivolt/{CurrentVersion}");
        return client;
    }

    private async Task CopyAssetAsync(HttpResponseMessage response, Stream output, long expectedSize, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long length && length != expectedSize)
            throw new InvalidDataException("Unexpected download size.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).AsTask().WaitAsync(TimeSpan.FromSeconds(60), token)) != 0)
        {
            total += count;
            if (total > expectedSize || total > MaximumPackageBytes) throw new InvalidDataException("Download is too large.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            var progress = (int)(total * 100 / expectedSize);
            if (progress == DownloadProgress) continue;
            DownloadProgress = progress;
            status = $"正在下载 macOS 安装包… {progress}%";
            Notify();
        }
        if (total != expectedSize) throw new InvalidDataException("Incomplete download.");
    }

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, long expectedSize, int maximum, CancellationToken token)
    {
        if (expectedSize > maximum) throw new InvalidDataException("Asset is too large.");
        if (response.Content.Headers.ContentLength is long length && length != expectedSize)
            throw new InvalidDataException("Unexpected asset size.");
        using var output = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token).AsTask().WaitAsync(TimeSpan.FromSeconds(60), token)) != 0)
        {
            if (output.Length + count > expectedSize) throw new InvalidDataException("Unexpected asset size.");
            output.Write(buffer, 0, count);
        }
        if (output.Length != expectedSize) throw new InvalidDataException("Incomplete asset.");
        return output.ToArray();
    }

    private static async Task<HttpResponseMessage> GetAssetAsync(HttpClient client, Uri initial, CancellationToken token)
    {
        var uri = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!AllowedDownloadUri(uri, initial)) throw new InvalidDataException("Invalid download redirect.");
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == 5) throw new InvalidDataException("Invalid download redirect.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Too many redirects.");
    }

    internal static bool AllowedDownloadUri(Uri uri, Uri initial) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com"
            || uri.Host == "github.com" && uri.AbsolutePath == initial.AbsolutePath && uri.Query.Length == 0);

    private static async Task<bool> VerifyFileAsync(string path, long size, string hash, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var actual = await SHA256.HashDataAsync(input, token);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(hash));
    }

    private static bool HashMatches(byte[] bytes, string hash) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(hash));

    internal static string? ParseChecksum(string text, string packageName, bool singleHashAllowed)
    {
        text = text.TrimStart('\uFEFF').Trim();
        if (singleHashAllowed && Regex.IsMatch(text, "^[0-9A-Fa-f]{64}$")) return text.ToLowerInvariant();
        var matches = Regex.Matches(text, @"^([0-9A-Fa-f]{64})[ \t]+\*?" + Regex.Escape(packageName) + @"[ \t]*\r?$", RegexOptions.Multiline);
        return matches.Count == 1 ? matches[0].Groups[1].Value.ToLowerInvariant() : null;
    }

    internal static ReleaseInfo? ParseRelease(string payload, string currentDisplayVersion)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "tag_name") is not { } tag
            || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Invalid release metadata.");
        var number = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Regex.IsMatch(number, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$") || !Version.TryParse(number, out var version)
            || !Version.TryParse(currentDisplayVersion.Split('+')[0].Split('-')[0], out var current))
            throw new InvalidDataException("Invalid release version.");
        if (version < current || version == current && !currentDisplayVersion.Contains('-')) return null;
        var pageUri = new Uri($"https://github.com/YangYingX/Perivolt/releases/tag/{Uri.EscapeDataString(tag)}");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return new(number, pageUri, null, null);
        if (assets.GetArrayLength() > 256) throw new InvalidDataException("Too many assets.");
        var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "arm64", Architecture.X64 => "x64", _ => null };
        if (architecture is null) return new(number, pageUri, null, null);
        var names = new[] { $"Perivolt-{number}-osx-{architecture}.zip", $"Perivolt-{number}-osx-{architecture}.dmg" };
        var package = names.Select(name => FindAsset(assets, tag, name, MaximumPackageBytes)).FirstOrDefault(asset => asset is not null);
        if (package is null) return new(number, pageUri, null, null);
        var checksum = new[] { package.Name + ".sha256", $"SHA256SUMS-{number}.txt", "SHA256SUMS.txt" }
            .Select(name => FindAsset(assets, tag, name, 64 * 1024)).FirstOrDefault(asset => asset is not null);
        return new(number, pageUri, package.Hash is not null || checksum is not null ? package : null, checksum);
    }

    private static AssetInfo? FindAsset(JsonElement assets, string tag, string name, long maximumSize)
    {
        var matches = assets.EnumerateArray().Where(asset => asset.ValueKind == JsonValueKind.Object && Text(asset, "name") == name).ToArray();
        if (matches.Length != 1) return null;
        var asset = matches[0];
        var uri = new Uri(DownloadPrefix + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));
        if (Text(asset, "state") != "uploaded" || Text(asset, "browser_download_url") != uri.AbsoluteUri
            || !asset.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number
            || !sizeValue.TryGetInt64(out var size) || size < 1 || size > maximumSize) return null;
        string? hash = null;
        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null)
        {
            if (digest.ValueKind != JsonValueKind.String || !Regex.IsMatch(digest.GetString()!, "^sha256:[0-9a-fA-F]{64}$")) return null;
            hash = digest.GetString()![7..].ToLowerInvariant();
        }
        return new(name, uri, size, hash);
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private void DeleteAttempt(string directory)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), cacheDirectory, StringComparison.Ordinal)
            || !Regex.IsMatch(Path.GetFileName(directory), "^download-[a-f0-9]{32}$")) return;
        try { Directory.Delete(directory, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal sealed record AssetInfo(string Name, Uri Uri, long Size, string? Hash);
    internal sealed record ReleaseInfo(string Version, Uri PageUri, AssetInfo? Package, AssetInfo? Checksum);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Dispose();
        CancelRequest();
    }
}
