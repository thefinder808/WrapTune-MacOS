using Avalonia;

namespace WrapTuneMacOS;

internal static class Program
{
    // Avalonia desktop entry point. Keep this minimal — see App for setup.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
