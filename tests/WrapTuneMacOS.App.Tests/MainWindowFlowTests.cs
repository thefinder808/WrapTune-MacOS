using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// End-to-end flow states driven through the real window and the real engine:
/// what the footer offers after success and after failure.
/// </summary>
public sealed class MainWindowFlowTests
{
    [AvaloniaFact]
    public void Success_offers_open_output_and_new_package_but_hides_package()
    {
        using var fixture = new FlowFixture();
        var w = fixture.RunPackageToCompletion();

        Assert.Equal("Done", w.FindControl<TextBlock>("TxtPercent")!.Text);
        // A visible Package button after success invites an accidental
        // re-run — the success actions are Open output and ← new package.
        Assert.False(w.FindControl<Button>("BtnPackage")!.IsVisible);
        Assert.True(w.FindControl<Button>("BtnOpenOutput")!.IsVisible);
        Assert.True(w.FindControl<Button>("BtnNewPackage")!.IsVisible);
        Assert.False(w.FindControl<Button>("BtnCancel")!.IsVisible);
    }

    [AvaloniaFact]
    public void Failure_keeps_package_visible_as_the_retry_action()
    {
        using var fixture = new FlowFixture();
        var w = fixture.RunPackageToCompletion();

        // Second run with overwrite off → deterministic engine failure.
        w.FindControl<Button>("BtnNewPackage")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        w.FindControl<ToggleSwitch>("ChkOverwrite")!.IsChecked = false;
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        w.FindControl<Button>("BtnPackage")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline &&
               w.FindControl<TextBlock>("TxtStageName")!.Text != "Failed")
            FlowFixture.Pump(TimeSpan.FromMilliseconds(50));

        Assert.Equal("Failed", w.FindControl<TextBlock>("TxtStageName")!.Text);
        var package = w.FindControl<Button>("BtnPackage")!;
        Assert.True(package.IsVisible);   // retry is the natural action on error
        Assert.True(package.IsEnabled);
        Assert.True(w.FindControl<Button>("BtnNewPackage")!.IsVisible);
    }

    /// <summary>Sandboxed settings + a tiny real payload, packaged for real.</summary>
    private sealed class FlowFixture : IDisposable
    {
        private readonly string _root;

        public FlowFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "wraptune-flow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            Directory.CreateDirectory(Path.Combine(_root, "out"));
            File.WriteAllText(Path.Combine(_root, "src", "setup.ps1"), "Write-Host hi");
            AppSettings.BaseDirOverride = _root;
            new AppSettings().Save();
        }

        public MainWindow RunPackageToCompletion()
        {
            var w = new MainWindow();
            w.Show();
            w.FindControl<TextBox>("TxtSourceFolder")!.Text = Path.Combine(_root, "src");
            w.FindControl<TextBox>("TxtSetupFile")!.Text = Path.Combine(_root, "src", "setup.ps1");
            w.FindControl<TextBox>("TxtOutputFolder")!.Text = Path.Combine(_root, "out");
            Pump(TimeSpan.FromMilliseconds(100));

            w.FindControl<Button>("BtnPackage")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline &&
                   w.FindControl<TextBlock>("TxtPercent")!.Text != "Done")
                Pump(TimeSpan.FromMilliseconds(50));
            return w;
        }

        public static void Pump(TimeSpan duration)
        {
            var until = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            AppSettings.BaseDirOverride = null;
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
