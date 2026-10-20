using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PowerHub.Native;
using PowerHub.Services;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;

namespace PowerHub;

// Only this small button receives input while the card's entire HWND is mouse-transparent.
internal sealed class WidgetUnlockWindow : Window
{
    internal WidgetUnlockWindow(Window owner, Action unlock)
    {
        Owner = owner; Width = Height = 26; ShowActivated = false; ShowInTaskbar = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
        var button = new System.Windows.Controls.Button { Padding = new Thickness(5), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        button.SetResourceReference(Control.BackgroundProperty, "AccentSoft");
        var glyph = new System.Windows.Shapes.Path { Data = Geometry.Parse("M2,6 H10 V13 H2 Z M3,6 V4 A3,3 0 0 1 9,4 V6 M6,9 V11"), Stretch = Stretch.Uniform, StrokeThickness = 1.4 };
        glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent"); button.Content = glyph;
        button.Click += (_, _) => unlock();
        Content = button;
        RefreshText();
        SourceInitialized += (_, _) => OverlayNative.SetMousePassthrough(this, false);
    }
    internal void RefreshText()
    {
        ((Control)Content).ToolTip = L.T("解锁位置");
        System.Windows.Automation.AutomationProperties.SetName((Control)Content, L.T("解锁位置"));
    }
}
