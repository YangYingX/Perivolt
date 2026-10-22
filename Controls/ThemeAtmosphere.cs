using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace PowerHub.Controls;

public sealed class ThemeAtmosphere : FrameworkElement
{
    public static readonly DependencyProperty EffectsEnabledProperty = DependencyProperty.Register(
        nameof(EffectsEnabled), typeof(bool), typeof(ThemeAtmosphere),
        new FrameworkPropertyMetadata(true, (sender, _) => ((ThemeAtmosphere)sender).UpdateActivity()));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(ThemeAtmosphere),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender,
            (sender, _) => ((ThemeAtmosphere)sender).glow = null));

    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1d / 24) };
    private readonly List<Ripple> ripples = [];
    private Window? host;
    private RadialGradientBrush? glow;
    private Point position;
    private Point target;
    private bool pointerInside;
    private double glowOpacity;
    private double lastFrame;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public bool EffectsEnabled { get => (bool)GetValue(EffectsEnabledProperty); set => SetValue(EffectsEnabledProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    private bool CanAnimate => EffectsEnabled && IsLoaded && IsVisible && host is { IsActive: true } &&
        host.WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation;

    public ThemeAtmosphere()
    {
        IsHitTestVisible = false;
        Focusable = false;
        timer.Tick += OnFrame;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateActivity();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (host is not null) return;
        host = Window.GetWindow(this);
        if (host is null) return;
        host.PreviewMouseMove += OnPointerMove;
        host.PreviewMouseDown += OnPointerDown;
        host.MouseLeave += OnPointerLeave;
        host.Activated += OnHostActivity;
        host.Deactivated += OnHostActivity;
        host.StateChanged += OnHostActivity;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        UpdateActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (host is not null)
        {
            host.PreviewMouseMove -= OnPointerMove;
            host.PreviewMouseDown -= OnPointerDown;
            host.MouseLeave -= OnPointerLeave;
            host.Activated -= OnHostActivity;
            host.Deactivated -= OnHostActivity;
            host.StateChanged -= OnHostActivity;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            host = null;
        }
        Clear();
    }

    private void OnHostActivity(object? sender, EventArgs e) => UpdateActivity();
    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateActivity();
    }

    private void UpdateActivity()
    {
        if (!CanAnimate) { Clear(); return; }
        MovePointer(Mouse.GetPosition(this));
    }

    private bool Contains(Point point) => new Rect(RenderSize).Contains(point);
    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (CanAnimate) MovePointer(e.GetPosition(this));
    }

    private void MovePointer(Point point)
    {
        pointerInside = Contains(point);
        if (pointerInside)
        {
            target = point;
            if (glowOpacity == 0) position = target;
        }
        if (pointerInside || glowOpacity > 0) StartFrames();
    }

    private void OnPointerLeave(object sender, MouseEventArgs e)
    {
        pointerInside = false;
        if (CanAnimate && glowOpacity > 0) StartFrames();
    }

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanAnimate || e.ChangedButton != MouseButton.Left) return;
        var point = e.GetPosition(this);
        if (!Contains(point)) return;
        MovePointer(point);
        if (ripples.Count == 3) ripples.RemoveAt(0);
        ripples.Add(new(point, Now));
        StartFrames();
    }

    private void StartFrames()
    {
        if (timer.IsEnabled) return;
        lastFrame = Now;
        timer.Start();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!CanAnimate) { Clear(); return; }
        var now = Now;
        var follow = 1 - Math.Exp(-Math.Clamp(now - lastFrame, 0, .1) * 16);
        lastFrame = now;
        position = new(position.X + (target.X - position.X) * follow, position.Y + (target.Y - position.Y) * follow);
        var opacityTarget = pointerInside ? 1d : 0d;
        glowOpacity += (opacityTarget - glowOpacity) * follow;
        var settled = (target - position).LengthSquared < .25 && Math.Abs(opacityTarget - glowOpacity) < .003;
        if (settled) { position = target; glowOpacity = opacityTarget; }
        ripples.RemoveAll(ripple => now - ripple.Started >= .75);
        InvalidateVisual();
        // A stationary pointer keeps its glow without leaving a rendering loop running.
        if (settled && ripples.Count == 0) timer.Stop();
    }

    private void Clear()
    {
        timer.Stop();
        ripples.Clear();
        pointerInside = false;
        glowOpacity = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!CanAnimate || ActualWidth <= 0 || ActualHeight <= 0 || AccentBrush is not SolidColorBrush accent) return;
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize), 13, 13));
        if (glowOpacity > 0)
        {
            if (glow is null)
            {
                var color = accent.Color;
                glow = new RadialGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(42, color.R, color.G, color.B), 0),
                    new(Color.FromArgb(16, color.R, color.G, color.B), .45),
                    new(Color.FromArgb(0, color.R, color.G, color.B), 1)
                });
                glow.Freeze();
            }
            drawingContext.PushOpacity(glowOpacity);
            drawingContext.DrawEllipse(glow, null, position, 155, 125);
            drawingContext.Pop();
        }
        var now = Now;
        foreach (var ripple in ripples)
        {
            var progress = Math.Clamp((now - ripple.Started) / .75, 0, 1);
            var radius = 8 + 86 * (1 - Math.Pow(1 - progress, 3));
            drawingContext.PushOpacity(.36 * Math.Pow(1 - progress, 2));
            drawingContext.DrawEllipse(null, new Pen(accent, 1.4), ripple.Origin, radius, radius);
            drawingContext.Pop();
        }
        drawingContext.Pop();
    }

    private sealed record Ripple(Point Origin, double Started);
}
