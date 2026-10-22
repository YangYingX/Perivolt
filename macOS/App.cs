using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace PowerHub.Mac;

public sealed class App : Application
{
    internal AppController? Controller { get; private set; }

    public override void Initialize()
    {
        Name = "Perivolt";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Controller = new AppController(desktop, Program.DataDirectory);
            desktop.Exit += (_, _) => Controller.Dispose();
            Controller.Start(Program.Arguments.Contains("--show"));
            if (Program.ArgumentValue(Program.Arguments, "--screenshot") is { } screenshot)
                _ = Controller.CaptureAsync(screenshot);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
