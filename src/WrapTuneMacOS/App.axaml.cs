using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace WrapTuneMacOS;

public partial class App : Application
{
    public override void Initialize()
    {
        // Drives the macOS application menu (the bold item next to the Apple
        // logo). Without it, Avalonia falls back to "Avalonia Application".
        // Kept in sync with CFBundleName in build-macos.sh's Info.plist.
        Name = "WrapTune";
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
