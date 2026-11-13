using Avalonia;
using System.Threading;

namespace LtzPrintBridge;

internal static class Program
{
    static Mutex? _single;
    [STAThread]
    public static int Main(string[] args)
    {
        if (Installer.Handle(args) is { } code) return code;
        _single = new Mutex(true, "Local\\LtzPrintBridge.SingleInstance", out bool first);
        if (!first) return 0; // already running (it lives in the system tray)
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
