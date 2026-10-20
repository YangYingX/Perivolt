using System.Windows;
using System.Windows.Media;
using PowerHub.Plugins;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using PowerHub.Services;
using System.Windows.Media.Imaging;
using System.IO;

namespace PowerHub.Controls;

// Visible rows load small local thumbnails; vector ornaments remain the no-image fallback.
public sealed class ThemeArtwork : FrameworkElement
{
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(nameof(Theme), typeof(PluginTheme), typeof(ThemeArtwork), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public PluginTheme? Theme { get => (PluginTheme?)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    public static readonly DependencyProperty ArtworkPathProperty = DependencyProperty.Register(nameof(ArtworkPath), typeof(string), typeof(ThemeArtwork), new PropertyMetadata(null, OnArtworkChanged));
    public string? ArtworkPath { get => (string?)GetValue(ArtworkPathProperty); set => SetValue(ArtworkPathProperty, value); }
    private BitmapSource? thumbnail;
    private long request;
    private static readonly object ThumbnailGate = new();
    private static readonly Dictionary<string, BitmapSource> Thumbnails = new();
    private static readonly Queue<string> ThumbnailOrder = new();

    public ThemeArtwork()
    {
        Loaded += (_, _) => { if (thumbnail is null && ArtworkPath is { } path) _ = LoadThumbnail(path); };
        Unloaded += (_, _) => { request++; thumbnail = null; };
    }

    private static void OnArtworkChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var artwork = (ThemeArtwork)source;
        artwork.request++; artwork.thumbnail = null; artwork.InvalidateVisual();
        if (artwork.IsLoaded && args.NewValue is string path) _ = artwork.LoadThumbnail(path);
    }

    private async Task LoadThumbnail(string path)
    {
        var revision = ++request;
        BitmapSource? image = null;
        try
        {
            image = await Task.Run(() =>
            {
                var key = path + File.GetLastWriteTimeUtc(path).Ticks;
                lock (ThumbnailGate)
                {
                    if (revision != request) return null;
                    if (Thumbnails.TryGetValue(key, out var cached)) return cached;
                    var decoded = ThemeBackdrop.Decode(path, 256);
                    while (ThumbnailOrder.Count >= 16) Thumbnails.Remove(ThumbnailOrder.Dequeue());
                    Thumbnails[key] = decoded; ThumbnailOrder.Enqueue(key);
                    return decoded;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException) { }
        if (Dispatcher.HasShutdownStarted) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (revision != request) return;
            thumbnail = image; InvalidateVisual();
        });
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (Theme is not { } theme || ActualWidth <= 0 || ActualHeight <= 0) return;
        drawing.PushClip(new RectangleGeometry(new Rect(RenderSize), 8, 8));
        drawing.DrawRectangle(Color(theme, "Surface"), null, new Rect(RenderSize));
        if (thumbnail is { } artwork)
        {
            var scale = Math.Max(ActualWidth / artwork.PixelWidth, ActualHeight / artwork.PixelHeight);
            var width = artwork.PixelWidth * scale; var height = artwork.PixelHeight * scale;
            drawing.DrawImage(artwork, new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height));
            drawing.Pop(); return;
        }
        drawing.PushTransform(new ScaleTransform(ActualWidth / 160, ActualHeight / 90));
        DrawMotif(drawing, theme);
        drawing.Pop();
        drawing.Pop();
    }

    public static Brush Decoration(PluginTheme? theme)
    {
        if (theme is null || string.IsNullOrEmpty(theme.Motif)) return Brushes.Transparent;
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) DrawMotif(context, theme);
        drawing.Freeze();
        var brush = new DrawingBrush(drawing) { Viewbox = new Rect(0, 0, 160, 90), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }

    private static Brush Color(PluginTheme theme, string key)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(theme.Colors.GetValueOrDefault(key, "#888888")));
        brush.Freeze();
        return brush;
    }

    private static void DrawMotif(DrawingContext drawing, PluginTheme theme)
    {
        var accent = Color(theme, "Accent");
        var secondary = Color(theme, "TextSecondary");
        var thin = new Pen(accent, 1.5); thin.Freeze();
        var faint = new Pen(secondary, .7); faint.Freeze();
        drawing.PushOpacity(.20);
        drawing.DrawEllipse(accent, null, new Point(132, 24), 45, 45);
        drawing.Pop();
        switch (theme.Motif)
        {
            case "Circuit":
                for (var i = 0; i < 4; i++)
                {
                    var y = 22 + i * 15;
                    drawing.DrawLine(i % 2 == 0 ? thin : faint, new Point(12, y), new Point(60 + i * 10, y));
                    drawing.DrawLine(thin, new Point(60 + i * 10, y), new Point(80 + i * 10, y - 18));
                    drawing.DrawEllipse(accent, null, new Point(80 + i * 10, y - 18), 2.5, 2.5);
                }
                break;
            case "Shard":
                drawing.DrawGeometry(null, thin, Geometry.Parse("M26,72 L58,14 112,22 80,80 Z M58,14 L80,80 M26,72 L112,22 M112,22 L144,60 80,80"));
                drawing.DrawLine(faint, new Point(8, 82), new Point(139, 8));
                break;
            case "Prism":
                drawing.DrawGeometry(null, thin, Geometry.Parse("M80,10 L115,45 80,80 45,45 Z M80,22 L103,45 80,68 57,45 Z M80,10 V80 M45,45 H115"));
                drawing.DrawEllipse(null, faint, new Point(80, 45), 47, 31);
                break;
            case "Orbit":
                drawing.DrawEllipse(null, thin, new Point(80, 45), 48, 27);
                drawing.PushTransform(new RotateTransform(-30, 80, 45));
                drawing.DrawEllipse(null, faint, new Point(80, 45), 58, 19);
                drawing.Pop();
                drawing.DrawEllipse(accent, null, new Point(124, 32), 4, 4);
                drawing.DrawGeometry(accent, null, Geometry.Parse("M72,29 L78,40 89,46 78,51 72,63 67,51 55,46 67,40 Z"));
                break;
            case "Pulse":
                for (var i = 0; i < 9; i++)
                {
                    var height = 12 + (i * 17 % 43);
                    drawing.DrawRoundedRectangle(i % 2 == 0 ? accent : secondary, null, new Rect(23 + i * 13, 45 - height / 2d, 5, height), 2, 2);
                }
                break;
            case "Crosshair":
                drawing.DrawEllipse(null, thin, new Point(80, 45), 24, 24);
                drawing.DrawEllipse(null, faint, new Point(80, 45), 35, 35);
                drawing.DrawGeometry(null, thin, Geometry.Parse("M80,6 V28 M80,62 V84 M40,45 H64 M96,45 H120 M20,20 H32 V32 M128,58 V70 H140"));
                drawing.DrawEllipse(accent, null, new Point(80, 45), 3, 3);
                break;
            default:
                drawing.DrawGeometry(null, thin, Geometry.Parse("M16,65 C35,6 57,84 80,35 S125,5 148,55 M16,76 C45,25 70,85 95,49 S130,16 148,66"));
                break;
        }
    }
}
