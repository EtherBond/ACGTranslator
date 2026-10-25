using System.Windows;
using ACGTranslator.Services;

namespace ACGTranslator;

public partial class App : Application
{
    public static SettingsStore Settings { get; } = new();
    public static HistoryStore History { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Window window = e.Args.Contains("--capture") ? new CaptureWindow() : e.Args.Contains("--calibrate") ? new CalibrationWindow(Settings.Load()) : new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
