using Avalonia;

namespace WrapTuneMacOS;

internal static class Program
{
    // Avalonia desktop entry point. Keep this minimal — see App for setup.
    [STAThread]
    public static void Main(string[] args)
    {
        // Last-resort diagnostics: an exception escaping an async void handler or
        // a dropped task otherwise kills the process with nothing written down.
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException +=
            (_, e) => { LogCrash(e.Exception); e.SetObserved(); };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Logs", "WrapTuneMacOS");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:o}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }
}
