using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerHub.Models;
using PowerHub.Plugins;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Application = System.Windows.Application;

namespace PowerHub.Services;

// Only the selected character is decoded. Files stay outside the application package.
public sealed class ThemeBackdrop : IDisposable
{
    private string? selectedPath;
    private BitmapSource? bitmap;
    private long revision;
    private bool disposed;
    private readonly object decodeGate = new();
    private ThemeArtworkPreference preference = new();
    public string Status { get; private set; } = "";
    public event Action? Changed;

    public static string? ResolvePath(AppSettings settings, PluginTheme? theme)
    {
        if (theme is null) return null;
        settings.ThemeArtwork.TryGetValue(settings.Theme, out var option);
        if (option?.Path is { } custom) return LocalPath(custom);
        if (settings.ThemeArtworkDirectory is not { } directory) return null;
        var root = LocalPath(directory);
        if (root is null) return null;
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            var file = Path.Combine(root, theme.Id + extension);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    private static string? LocalPath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public void Apply(AppSettings settings, PluginTheme? theme)
    {
        preference = settings.ThemeArtwork.GetValueOrDefault(settings.Theme) ?? new();
        var path = preference.Enabled ? ResolvePath(settings, theme) : null;
        if (theme is null) path = null;
        if (path == selectedPath)
        {
            Publish();
            return;
        }
        selectedPath = path;
        bitmap = null;
        var request = ++revision;
        Status = path is null ? "当前主题未找到立绘。" : "正在加载立绘…";
        Publish();
        if (path is null) return;
        _ = LoadAsync(path, request);
    }

    private async Task LoadAsync(string path, long request)
    {
        BitmapSource? loaded = null;
        try
        {
            loaded = await Task.Run(() =>
            {
                lock (decodeGate) return disposed || request != revision ? null : Decode(path);
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException) { }
        var dispatcher = Application.Current?.Dispatcher;
        if (disposed || dispatcher is null || dispatcher.HasShutdownStarted) return;
        await dispatcher.InvokeAsync(() =>
        {
            if (disposed || request != revision) return;
            bitmap = loaded;
            Status = loaded is null ? "立绘无法读取，请使用 PNG 或 JPEG 图片。" : "立绘已加载，仅在本地使用。";
            Publish();
            Changed?.Invoke();
        });
    }

    private static BitmapSource Decode(string path)
    {
        if (new FileInfo(path).Length > 25 * 1024 * 1024) throw new InvalidDataException("Image exceeds 25 MiB");
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 64_000_000 || frame.PixelWidth > 16384 || frame.PixelHeight > 16384)
            throw new InvalidDataException("Image dimensions exceed limit");
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(1600, frame.PixelWidth);
        else image.DecodePixelHeight = Math.Min(1600, frame.PixelHeight);
        image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }

    private void Publish()
    {
        if (disposed || Application.Current is null) return;
        Brush brush = Brushes.Transparent;
        if (bitmap is not null)
        {
            var image = new ImageBrush(bitmap)
            {
                Stretch = Stretch.Uniform, AlignmentY = AlignmentY.Bottom,
                AlignmentX = preference.Position switch { "Left" => AlignmentX.Left, "Center" => AlignmentX.Center, _ => AlignmentX.Right },
                Opacity = preference.Opacity
            };
            image.Freeze(); brush = image;
        }
        Application.Current.Resources["ThemeBackdrop"] = brush;
        if (Application.Current.Resources["Surface"] is SolidColorBrush surface)
        {
            var card = surface.Clone(); card.Opacity = bitmap is null ? 1 : .78; card.Freeze();
            Application.Current.Resources["ThemeCardSurface"] = card;
        }
    }

    public void Dispose() { disposed = true; revision++; bitmap = null; }
    public void Reload() { selectedPath = ""; revision++; }
}
