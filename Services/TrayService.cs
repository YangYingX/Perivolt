using PowerHub.Services;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using PowerHub.Models;
using Forms = System.Windows.Forms;

namespace PowerHub.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private TrayMenu? _menu;
    private IReadOnlyList<DeviceStatus> _devices = [];
    private readonly Action<string> _command;
    private readonly Action _settingsChanged;
    private readonly AppSettings _settings;
    private readonly ReminderPolicy _reminderPolicy = new();
    private readonly Icon _batteryIcon;
    private readonly HwndSource _menuOwner;
    private readonly FrameworkElement _menuTarget = new();
    private bool _disposed;

    public TrayService(Action show, Action refresh, Action exit, Action openWidgetSettings, Action openSettings, AppSettings settings, Action settingsChanged, Action<string>? applyScene = null, Action? openAutomation = null)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _command = command =>
        {
            switch (command)
            {
                case "show": show(); break;
                case "widget-settings": openWidgetSettings(); break;
                case "settings": openSettings(); break;
                case "refresh": refresh(); break;
                case "exit": exit(); break;
                case "automation": openAutomation?.Invoke(); break;
                default: if (command.StartsWith("scene:", StringComparison.Ordinal)) applyScene?.Invoke(command[6..]); break;
            }
        };
        _menuOwner = new HwndSource(new HwndSourceParameters("Perivolt.TrayMenuOwner")
        {
            WindowStyle = unchecked((int)0x90000000), // WS_POPUP | WS_VISIBLE.
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW.
            Width = 1, Height = 1, PositionX = -32000, PositionY = -32000,
            UsesPerPixelTransparency = true
        }) { RootVisual = _menuTarget };
        _batteryIcon = CreateBatteryIcon();
        _icon = new Forms.NotifyIcon { Icon = _batteryIcon, Text = L.T("PowerHub · 外设电量中心"), Visible = true };
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) show();
            else if (args.Button == Forms.MouseButtons.Right)
                _menuTarget.Dispatcher.BeginInvoke(new Action(OpenMenu), DispatcherPriority.ContextIdle);
        };
        _icon.BalloonTipClicked += (_, _) => show();
    }

    private void OpenMenu()
    {
        if (_disposed) return;
        if (_menu is not null) _menu.IsOpen = false;
        _menu = new TrayMenu(_settings, _command, _settingsChanged);
        _menu.Refresh(_devices);
        _menu.PlacementTarget = _menuTarget;
        _menu.Placement = PlacementMode.MousePoint;
        // Finish the shell's click notification, then activate the owner before WPF takes capture.
        SetForegroundWindow(_menuOwner.Handle);
        _menu.IsOpen = true;
        _menu.Focus();
    }

    public void ApplySettings()
    {
        if (_disposed) return;
        _menu?.Refresh(_devices);
        var devices = _devices.Where(device => device.ConnectionKnown && device.IsConnected && device.HasBattery).Take(2).ToArray();
        _icon.Text = devices.Length == 0 ? L.T("PowerHub · 外设电量中心") : Shorten(L.AppName + " · " +
            string.Join(" · ", devices.Select(device => $"{Shorten(DisplayName(device), 14)} {Shorten(device.BatteryText, 8)}")), 63);
    }

    public void Update(ScanResult result, DateTimeOffset scanStartedAt, bool suppressReminders = false)
    {
        if (_disposed) return;
        _devices = result.Devices;
        ApplySettings();
        var reminders = suppressReminders ? [] : _reminderPolicy.Evaluate(result, scanStartedAt, _settings, DateTimeOffset.Now);
        if (reminders.Count > 0)
        {
            var message = string.Join("\n", reminders.Take(5).Select(device => L.F("{0} · {1}，请充电", Shorten(DisplayName(device), 40), device.BatteryText)));
            if (reminders.Count > 5) message += L.F("\n还有 {0} 个设备需要充电", reminders.Count - 5);
            _icon.ShowBalloonTip(6000, L.T("设备需要充电"), message, Forms.ToolTipIcon.Warning);
        }

    }

    public void ShowPluginNotification(string title, string message)
    {
        if (!_disposed) _icon.ShowBalloonTip(6000, Shorten(title, 63), Shorten(message, 250), Forms.ToolTipIcon.Info);
    }

    private string DisplayName(DeviceStatus device) => _settings.GetDeviceName(device);

    private static string Shorten(string text, int length)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length <= length) return text;
        var end = length - 1;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }

    private static Icon CreateBatteryIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("/PowerHub;component/Assets/powerhub.ico", UriKind.Relative));
        using var stream = resource.Stream;
        using var icon = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.Visible = false;
            if (_menu is not null) { _menu.StaysOpen = true; _menu.IsOpen = false; }
            _menuOwner.Dispose();
        }
        finally
        {
            _icon.Dispose();
            _batteryIcon.Dispose();
        }
    }
}
