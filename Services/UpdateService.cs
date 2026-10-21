using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace PowerHub.Services;

public sealed class UpdateService : IDisposable
{
    private const string Endpoint = "https://api.github.com/repos/YangYingX/Perivolt/releases/latest";
    private const string DownloadPrefix = "https://github.com/YangYingX/Perivolt/releases/download/";
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(12) };
    private readonly string cacheDirectory;
    private readonly string currentDisplayVersion;
    private readonly Func<HttpClient> createClient;
    private CancellationTokenSource? request;
    private ReleaseInfo? release;
    private string? readyPath;
    private bool disposed;
    private bool downloadCancelled;
    private bool pendingCheck;
    private string? notifiedVersion;
    public static string CurrentVersion => typeof(Program).Assembly.GetName().Version!.ToString(3);
    public static string DisplayVersion => typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? CurrentVersion;
    public bool IsEnabled { get; private set; }
    public bool IsChecking => request is not null && !IsDownloading;
    public bool IsDownloading { get; private set; }
    public bool IsBusy => request is not null;
    public int DownloadProgress { get; private set; }
    public bool CanDownloadInstaller => IsEnabled && release?.Installer is not null;
    public string? AvailableVersion => release?.Version;
    private string statusKey = "离线模式 · 更新检查已关闭";
    private object?[] statusArguments = [];
    public string Status => L.F(statusKey, statusArguments);
    private void SetStatus(string key, params object?[] arguments) { statusKey = key; statusArguments = arguments; }
    public Uri? ReleaseUri => release?.PageUri;
    public event Action? Changed;
    public event Action<string>? UpdateAvailable;

    public UpdateService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerHub", "updates"),
        DisplayVersion, () => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 256 * 1024 }) { }

    internal UpdateService(string cacheDirectory, string currentDisplayVersion, Func<HttpClient> createClient)
    {
        this.cacheDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
        this.currentDisplayVersion = currentDisplayVersion;
        this.createClient = createClient;
        timer.Tick += (_, _) => _ = CheckAsync();
    }

    public void Configure(bool enabled)
    {
        if (disposed || IsEnabled == enabled) return;
        IsEnabled = enabled;
        if (enabled)
        {
            timer.Start();
            pendingCheck = IsBusy;
            _ = CheckAsync();
        }
        else
        {
            timer.Stop();
            pendingCheck = false;
            request?.Cancel();
            release = null;
            readyPath = null;
            SetStatus("离线模式 · 更新检查已关闭");
        }
        Changed?.Invoke();
    }

    public async Task CheckAsync()
    {
        if (disposed || !IsEnabled || IsBusy) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        request = cancellation;
        SetStatus("正在检查 GitHub 上的新版本…");
        Changed?.Invoke();
        try
        {
            // Neither constructing the service nor collecting devices opens a connection.
            using var client = CreateClient();
            using var message = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            message.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(message, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                release = null;
                SetStatus("暂时没有公开发行版");
                return;
            }
            response.EnsureSuccessStatusCode();
            var found = ParseRelease(await response.Content.ReadAsStringAsync(cancellation.Token), currentDisplayVersion);
            cancellation.Token.ThrowIfCancellationRequested();
            if (disposed || !IsEnabled) return;
            if (release != found) readyPath = null;
            release = found;
            if (found is null) SetStatus("已是最新版本 · v{0}", currentDisplayVersion);
            else SetStatus(found.Installer is null ? "新版本未提供可验证的安装包，请前往发行页下载。" : "发现新版本 v{0}，可下载并安装", found.Version);
            if (found is not null && notifiedVersion != found.Version)
            {
                notifiedVersion = found.Version;
                UpdateAvailable?.Invoke(found.Version);
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            if (IsEnabled && !disposed) SetStatus("暂时无法检查更新，设备监测继续运行。可稍后重试。");
        }
        finally
        {
            request = null;
            if (!disposed) Changed?.Invoke();
            if (pendingCheck && IsEnabled && !disposed) { pendingCheck = false; _ = CheckAsync(); }
        }
    }

    public async Task<string?> DownloadInstallerAsync()
    {
        if (disposed || !IsEnabled || IsBusy || release?.Installer is not { } installer) return null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        request = cancellation;
        IsDownloading = true;
        downloadCancelled = false;
        DownloadProgress = 0;
        string? attemptDirectory = null;
        var completed = false;
        try
        {
            SetStatus("正在获取安装包校验信息…");
            Changed?.Invoke();
            using var client = CreateClient();
            var hash = installer.Hash;
            if (hash is null && release.Checksum is { } checksum)
            {
                using var checksumResponse = await GetAssetAsync(client, checksum.Uri, cancellation.Token);
                var bytes = await ReadBytesAsync(checksumResponse, checksum.Size, 64 * 1024, cancellation.Token);
                if (checksum.Hash is not null && !HashMatches(bytes, checksum.Hash)) throw new InvalidDataException("Invalid checksum asset.");
                hash = ParseChecksum(new UTF8Encoding(false, true).GetString(bytes), installer.Name, checksum.Name == installer.Name + ".sha256");
            }
            if (hash is null) throw new InvalidDataException("Missing installer checksum.");
            cancellation.Token.ThrowIfCancellationRequested();
            if (readyPath is not null && await VerifyFileAsync(readyPath, installer.Size, hash, cancellation.Token))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                SetStatus("安装包已校验，正在打开安装向导…");
                return readyPath;
            }
            readyPath = null;
            attemptDirectory = Path.Combine(cacheDirectory, "download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attemptDirectory);
            var partialPath = Path.Combine(attemptDirectory, installer.Name + ".part");
            SetStatus("正在下载安装包… {0}%", 0);
            Changed?.Invoke();
            using (var response = await GetAssetAsync(client, installer.Uri, cancellation.Token))
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
                await CopyAssetAsync(response, output, installer.Size, cancellation.Token);
            SetStatus("正在校验安装包…");
            Changed?.Invoke();
            if (!await VerifyFileAsync(partialPath, installer.Size, hash, cancellation.Token)) throw new InvalidDataException("Installer verification failed.");
            cancellation.Token.ThrowIfCancellationRequested();
            if (disposed || !IsEnabled) return null;
            readyPath = Path.Combine(attemptDirectory, installer.Name);
            File.Move(partialPath, readyPath);
            completed = true;
            SetStatus("安装包已校验，正在打开安装向导…");
            return readyPath;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or TimeoutException or InvalidDataException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (IsEnabled && !disposed) SetStatus(downloadCancelled ? "下载已取消，当前版本继续运行。" : "下载或校验未完成，当前版本继续运行。可重试或前往发行页下载。");
            return null;
        }
        finally
        {
            if (!completed && attemptDirectory is not null) DeleteAttempt(attemptDirectory);
            IsDownloading = false;
            request = null;
            if (!disposed) Changed?.Invoke();
            if (pendingCheck && IsEnabled && !disposed) { pendingCheck = false; _ = CheckAsync(); }
        }
    }

    public void CancelDownload()
    {
        if (!IsDownloading) return;
        downloadCancelled = true;
        request?.Cancel();
    }

    public void InstallationFailed(bool settingsSaveFailed)
    {
        SetStatus(settingsSaveFailed ? "设置未保存，已暂停安装。请检查权限后重试。" : "未能打开安装器。可重试，或前往发行页手动下载。");
        Changed?.Invoke();
    }

    private HttpClient CreateClient()
    {
        var client = createClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Perivolt/{CurrentVersion}");
        return client;
    }

    private async Task CopyAssetAsync(HttpResponseMessage response, Stream output, long expectedSize, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long length && length != expectedSize) throw new InvalidDataException("Unexpected download size.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).AsTask().WaitAsync(TimeSpan.FromSeconds(60), token)) != 0)
        {
            total += count;
            if (total > expectedSize) throw new InvalidDataException("Download is too large.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            var progress = (int)(total * 100 / expectedSize);
            if (progress != DownloadProgress)
            {
                DownloadProgress = progress;
                SetStatus("正在下载安装包… {0}%", progress);
                Changed?.Invoke();
            }
        }
        if (total != expectedSize) throw new InvalidDataException("Incomplete download.");
    }

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, long expectedSize, int maximum, CancellationToken token)
    {
        if (expectedSize > maximum) throw new InvalidDataException("Asset is too large.");
        if (response.Content.Headers.ContentLength is long length && length != expectedSize) throw new InvalidDataException("Unexpected asset size.");
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
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
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

    internal static bool AllowedDownloadUri(Uri uri, Uri initial) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && (uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com"
        || uri.Host == "github.com" && uri.AbsolutePath == initial.AbsolutePath && uri.Query.Length == 0);

    private static async Task<bool> VerifyFileAsync(string path, long size, string hash, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var header = new byte[2];
        if (await input.ReadAsync(header, token) != 2 || header[0] != 'M' || header[1] != 'Z') return false;
        input.Position = 0;
        var actual = await SHA256.HashDataAsync(input, token);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(hash));
    }

    private static bool HashMatches(byte[] bytes, string hash) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(hash));

    internal static string? ParseChecksum(string text, string installerName, bool singleHashAllowed)
    {
        text = text.TrimStart('\uFEFF').Trim();
        if (singleHashAllowed && Regex.IsMatch(text, "^[0-9A-Fa-f]{64}$")) return text.ToLowerInvariant();
        var matches = Regex.Matches(text, @"^([0-9A-Fa-f]{64})[ \t]+\*?" + Regex.Escape(installerName) + @"[ \t]*\r?$", RegexOptions.Multiline);
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
            || !Version.TryParse(currentDisplayVersion.Split('+')[0].Split('-')[0], out var current)) throw new InvalidDataException("Invalid release version.");
        if (version < current || version == current && !currentDisplayVersion.Contains('-')) return null;
        var pageUri = new Uri($"https://github.com/YangYingX/Perivolt/releases/tag/{Uri.EscapeDataString(tag)}");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return new(number, pageUri, null, null);
        if (assets.GetArrayLength() > 256) throw new InvalidDataException("Too many assets.");
        var names = new[] { $"Perivolt-Setup-{number}-x64.exe", $"PowerHub-Setup-{number}-x64.exe" };
        var installer = names.Select(name => FindAsset(assets, tag, name, MaximumInstallerBytes)).FirstOrDefault(asset => asset is not null);
        if (installer is null) return new(number, pageUri, null, null);
        var checksum = new[] { installer.Name + ".sha256", $"SHA256SUMS-{number}.txt", "SHA256SUMS.txt" }
            .Select(name => FindAsset(assets, tag, name, 64 * 1024)).FirstOrDefault(asset => asset is not null);
        return new(number, pageUri, installer.Hash is not null || checksum is not null ? installer : null, checksum);
    }

    private static AssetInfo? FindAsset(JsonElement assets, string tag, string name, long maximumSize)
    {
        var matches = assets.EnumerateArray().Where(asset => asset.ValueKind == JsonValueKind.Object && Text(asset, "name") == name).ToArray();
        if (matches.Length != 1) return null;
        var asset = matches[0];
        var uri = new Uri(DownloadPrefix + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name));
        if (Text(asset, "state") != "uploaded" || Text(asset, "browser_download_url") != uri.AbsoluteUri || !asset.TryGetProperty("size", out var sizeValue)
            || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size) || size < 1 || size > maximumSize) return null;
        string? hash = null;
        if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null)
        {
            if (digest.ValueKind != JsonValueKind.String || !Regex.IsMatch(digest.GetString()!, "^sha256:[0-9a-fA-F]{64}$")) return null;
            hash = digest.GetString()![7..].ToLowerInvariant();
        }
        return new(name, uri, size, hash);
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private void DeleteAttempt(string directory)
    {
        // Only this invocation's freshly generated directory is removed, never a shared cache tree.
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), cacheDirectory, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(Path.GetFileName(directory), "^download-[a-f0-9]{32}$")) return;
        try { Directory.Delete(directory, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal sealed record AssetInfo(string Name, Uri Uri, long Size, string? Hash);
    internal sealed record ReleaseInfo(string Version, Uri PageUri, AssetInfo? Installer, AssetInfo? Checksum);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        request?.Cancel();
    }
}
