using Avalonia;
using Avalonia.Headless;
using WrapTuneMacOS;
using WrapTuneMacOS.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace WrapTuneMacOS.UiTests;

/// <summary>Configures the real <see cref="App"/> on Avalonia's headless platform.
/// Skia (not the headless drawing stub) is required: the app ships embedded
/// fonts (Space Grotesk / JetBrains Mono), and only a real font manager can
/// load them — it also lets tests capture rendered frames.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
