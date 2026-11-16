using Avalonia;
using ReactiveUI.Avalonia;
using Avalonia.X11;

namespace SHARD;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .With(new X11PlatformOptions { OverlayPopups = true })
                  .LogToTrace()
                  .UseReactiveUI(_ => { }); // default activation, property and command binding
}