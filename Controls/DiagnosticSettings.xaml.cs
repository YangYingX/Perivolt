using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PowerHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerHub.Controls;

public partial class DiagnosticSettings : UserControl
{
    private AppController? controller;
    private bool previewOpen;

    public DiagnosticSettings() => InitializeComponent();

    public void Initialize(AppController value)
    {
        controller = value;
        Loaded += (_, _) => { controller.DiagnosticReports.Changed -= Refresh; controller.DiagnosticReports.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => controller.DiagnosticReports.Changed -= Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (controller is null || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        var reports = controller.DiagnosticReports;
        EnabledBox.IsChecked = controller.Settings.AutoReportDiagnostics;
        EnabledBox.IsEnabled = reports.IsConfigured;
        UploadButton.IsEnabled = reports.IsConfigured && !reports.IsUploading && !previewOpen;
        StatusText.Text = reports.Status;
        IssueButton.Visibility = reports.LastIssueUri is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (controller is null) return;
        controller.Settings.AutoReportDiagnostics = EnabledBox.IsChecked == true;
        controller.ApplySettings();
        Refresh();
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (controller is null || !controller.DiagnosticReports.IsConfigured || controller.DiagnosticReports.IsUploading || previewOpen) return;
        var report = controller.CreateDiagnosticReport();
        if (report is null) { StatusText.Text = L.T("请先刷新设备，再预览诊断报告。"); return; }
        previewOpen = true;
        Refresh();
        try
        {
            if (ConfirmUpload(report.Json)) await controller.DiagnosticReports.UploadAsync(report);
        }
        finally { previewOpen = false; Refresh(); }
    }

    private bool ConfirmUpload(string json)
    {
        var dialog = new Window
        {
            Title = L.T("预览诊断报告"), Owner = Window.GetWindow(this), Width = 720, Height = 580,
            MinWidth = 420, MinHeight = 320, MaxHeight = SystemParameters.WorkArea.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        dialog.SetResourceReference(BackgroundProperty, "Surface");
        dialog.SetResourceReference(ForegroundProperty, "TextPrimary");
        var content = new Grid { Margin = new Thickness(22) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var explanation = new TextBlock
        {
            Text = L.T("点击发送后，下方内容会经项目接收服务发布到 YangYingX/Perivolt 的公开 GitHub 问题区，任何人都能查看。取消不会上传。"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16), FontSize = 13
        };
        content.Children.Add(explanation);
        var preview = new TextBox
        {
            Text = json, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Top,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(10)
        };
        Grid.SetRow(preview, 1);
        content.Children.Add(preview);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = L.T("取消"), IsCancel = true, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 10, 0) };
        var send = new Button { Content = L.T("发送到公开问题区"), Padding = new Thickness(16, 8, 16, 8) };
        send.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(send);
        Grid.SetRow(actions, 2);
        content.Children.Add(actions);
        dialog.Content = content;
        return dialog.ShowDialog() == true;
    }

    private void OnOpenIssue(object sender, RoutedEventArgs e)
    {
        if (controller?.DiagnosticReports.LastIssueUri is not { } uri) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = L.T("未能打开浏览器，请前往 GitHub 的 YangYingX/Perivolt 问题区查看。"); }
    }
}
