using Avalonia;
using Avalonia.Headless;
using WrapTuneMacOS;
using WrapTuneMacOS.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace WrapTuneMacOS.UiTests;

/// <summary>Configures the real <see cref="App"/> on Avalonia's headless platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
