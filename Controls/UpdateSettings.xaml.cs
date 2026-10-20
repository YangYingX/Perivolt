using System.Diagnostics;
using System.Windows;
using PowerHub.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerHub.Controls;

public partial class UpdateSettings : UserControl
{
    private AppController? controller;

    public UpdateSettings() => InitializeComponent();

    public void Initialize(AppController value)
    {
        controller = value;
        Loaded += (_, _) => { controller.Updates.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => controller.Updates.Changed -= Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (controller is null) return;
        VersionText.Text = $"v{UpdateService.CurrentVersion}";
        EnabledBox.IsChecked = controller.Settings.AutoCheckUpdates;
        StatusText.Text = controller.Updates.Status;
        CheckButton.IsEnabled = controller.Updates.IsEnabled && !controller.Updates.IsChecking;
        DownloadButton.Visibility = controller.Updates.IsEnabled && controller.Updates.ReleaseUri is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        controller.Settings.AutoCheckUpdates = EnabledBox.IsChecked == true;
        controller.ApplySettings();
        Refresh();
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        if (controller is not null) await controller.Updates.CheckAsync();
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if (controller?.Updates is not { IsEnabled: true, ReleaseUri: { } uri }) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = L.T("未能打开浏览器，请在 GitHub 的 YangYingX/PowerHub 发行页下载。"); }
    }
}
