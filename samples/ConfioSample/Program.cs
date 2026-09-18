using System;
using Avalonia;

namespace ConfioSample;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
