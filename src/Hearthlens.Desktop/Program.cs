using Avalonia;
using Avalonia.Controls;

namespace Hearthlens.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--render-ui")
            {
                // Developer check of the UI: renders to PNGs off-screen, nothing is shown.
                return OffscreenRenderer.Run(args[i + 1]);
            }
        }

        if (args.Contains("--version"))
        {
            Console.WriteLine("Hearthlens " + UI.AppHost.Version);
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DesktopApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
