using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Expander = System.Windows.Controls.Expander;

namespace PowerHub.Controls;

public sealed class AnimatedExpander : Expander
{
    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.Register(
        nameof(AnimationsEnabled), typeof(bool), typeof(AnimatedExpander),
        new PropertyMetadata(true, (owner, _) =>
        {
            var expander = (AnimatedExpander)owner;
            if (!expander.AnimationsEnabled) expander.ApplyState();
        }));

    private FrameworkElement? clip;
    private ContentPresenter? presenter;
    private ToggleButton? header;
    private int transition;

    public bool AnimationsEnabled
    {
        get => (bool)GetValue(AnimationsEnabledProperty);
        set => SetValue(AnimationsEnabledProperty, value);
    }

    public AnimatedExpander() => Unloaded += (_, _) => ApplyState();

    public override void OnApplyTemplate()
    {
        ApplyState();
        base.OnApplyTemplate();
        clip = GetTemplateChild("PART_Clip") as FrameworkElement;
        presenter = GetTemplateChild("PART_Content") as ContentPresenter;
        header = GetTemplateChild("PART_Header") as ToggleButton;
        ApplyState();
    }

    protected override void OnExpanded() { base.OnExpanded(); ChangeState(); }
    protected override void OnCollapsed() { base.OnCollapsed(); ChangeState(); }

    private void ChangeState()
    {
        if (!IsLoaded || !AnimationsEnabled || clip is null || presenter is null) { ApplyState(); return; }
        var height = double.IsNaN(clip.Height) ? clip.ActualHeight : clip.Height;
        var opacity = clip.Opacity;
        StopAnimation();
        clip.Visibility = Visibility.Visible;
        SetContentInteraction();
        presenter.Measure(new System.Windows.Size(Math.Max(0, ActualWidth), double.PositiveInfinity));
        var target = IsExpanded ? presenter.DesiredSize.Height : 0;
        var revision = transition;
        var duration = TimeSpan.FromMilliseconds(180);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var resize = new DoubleAnimation(height, target, duration) { EasingFunction = easing };
        var fade = new DoubleAnimation(opacity, IsExpanded ? 1 : 0, duration) { EasingFunction = easing };
        Timeline.SetDesiredFrameRate(resize, 60);
        Timeline.SetDesiredFrameRate(fade, 60);
        resize.Completed += (_, _) => { if (revision == transition) ApplyState(); };
        clip.BeginAnimation(HeightProperty, resize, HandoffBehavior.SnapshotAndReplace);
        clip.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetContentInteraction()
    {
        if (presenter is null) return;
        if (!IsExpanded && presenter.IsKeyboardFocusWithin) header?.Focus();
        presenter.IsEnabled = IsExpanded;
        presenter.IsHitTestVisible = IsExpanded;
        KeyboardNavigation.SetTabNavigation(presenter, IsExpanded ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
    }

    private void ApplyState()
    {
        StopAnimation();
        if (clip is null) return;
        clip.Height = IsExpanded ? double.NaN : 0;
        clip.Opacity = IsExpanded ? 1 : 0;
        clip.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        SetContentInteraction();
    }

    private void StopAnimation()
    {
        transition++;
        clip?.BeginAnimation(HeightProperty, null);
        clip?.BeginAnimation(OpacityProperty, null);
    }
}
