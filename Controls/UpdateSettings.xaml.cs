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
        Loaded += (_, _) => { controller.Updates.Changed -= Refresh; controller.Updates.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => controller.Updates.Changed -= Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (controller is null || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        var updates = controller.Updates;
        VersionText.Text = $"v{UpdateService.DisplayVersion}";
        EnabledBox.IsChecked = controller.Settings.AutoCheckUpdates;
        StatusText.Text = updates.Status;
        InstallDirectoryText.Text = controller.UpdateInstallDirectory is { } directory
            ? L.F("安装目录：{0}", directory) : L.T("便携运行：在安装向导中确认安装目录。");
        CheckButton.IsEnabled = updates.IsEnabled && !updates.IsBusy;
        var releaseVisibility = updates.IsEnabled && updates.ReleaseUri is not null ? Visibility.Visible : Visibility.Collapsed;
        DownloadButton.Visibility = ReleaseButton.Visibility = releaseVisibility;
        DownloadButton.IsEnabled = updates.IsEnabled && updates.CanDownloadInstaller && !updates.IsBusy;
        DownloadButton.Content = updates.AvailableVersion is { } version ? L.F("下载并安装 v{0}", version) : L.T("下载并安装");
        DownloadProgressPanel.Visibility = updates.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgressBar.Value = updates.DownloadProgress;
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

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (controller is not null) await controller.DownloadAndInstallUpdateAsync();
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e) => controller?.Updates.CancelDownload();

    private void OnOpenRelease(object sender, RoutedEventArgs e)
    {
        if (controller?.Updates is not { IsEnabled: true, ReleaseUri: { } uri }) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = L.T("未能打开浏览器，请在 GitHub 的 YangYingX/Perivolt 发行页下载。"); }
    }
}
