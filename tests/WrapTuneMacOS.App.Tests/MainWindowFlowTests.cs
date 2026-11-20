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
        Assert.Equal("Packaged", w.FindControl<TextBlock>("TxtRunTitle")!.Text);
        // The file name leads the strip, so trimming can never hide it.
        Assert.StartsWith("setup.intunewin", w.FindControl<TextBlock>("TxtBreadcrumb")!.Text);
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
        // The hero no longer keeps showing the percentage the run died at.
        Assert.Equal("Failed", w.FindControl<TextBlock>("TxtPercent")!.Text);
        Assert.Equal("Packaging failed", w.FindControl<TextBlock>("TxtRunTitle")!.Text);
        var package = w.FindControl<Button>("BtnPackage")!;
        Assert.True(package.IsVisible);   // retry is the natural action on error
        Assert.True(package.IsEnabled);
        Assert.True(w.FindControl<Button>("BtnNewPackage")!.IsVisible);
    }

    [AvaloniaFact]
    public void Output_name_follows_the_setup_file_and_uses_the_folder_for_psadt()
    {
        using var fixture = new FlowFixture();
        var w = new MainWindow();
        w.Show();
        var name = w.FindControl<TextBox>("TxtOutputName")!;
        var source = Path.Combine(fixture.Root, "Zoom 6.1");

        w.FindControl<TextBox>("TxtSourceFolder")!.Text = source;
        w.FindControl<TextBox>("TxtSetupFile")!.Text = Path.Combine(source, "setup.msi");
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        Assert.Equal("setup", name.Text);

        w.FindControl<TextBox>("TxtSetupFile")!.Text = Path.Combine(source, "Invoke-AppDeployToolkit.exe");
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        Assert.Equal("Zoom 6.1", name.Text);

        // A hand-typed name must not carry over to the next package.
        name.Text = "Custom";
        w.FindControl<TextBox>("TxtSourceFolder")!.Text = Path.Combine(fixture.Root, "Chrome 130");
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        Assert.Equal("Chrome 130", name.Text);
    }

    [AvaloniaFact]
    public void An_edited_output_name_names_the_package_and_a_bad_one_blocks_packaging()
    {
        using var fixture = new FlowFixture();
        var w = fixture.RunPackageToCompletion(outputName: "Zoom 6.1");

        Assert.True(File.Exists(Path.Combine(fixture.Root, "out", "Zoom 6.1.intunewin")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "out", "setup.intunewin")));

        w.FindControl<Button>("BtnNewPackage")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        w.FindControl<TextBox>("TxtOutputName")!.Text = "bad/name";
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        Assert.False(w.FindControl<Button>("BtnPackage")!.IsEnabled);
    }

    [AvaloniaFact]
    public void A_setup_file_outside_the_source_folder_is_caught_before_packaging()
    {
        using var fixture = new FlowFixture();
        var elsewhere = Path.Combine(fixture.Root, "elsewhere.ps1");
        File.WriteAllText(elsewhere, "Write-Host hi");
        var w = new MainWindow();
        w.Show();

        w.FindControl<TextBox>("TxtSourceFolder")!.Text = Path.Combine(fixture.Root, "src");
        w.FindControl<TextBox>("TxtSetupFile")!.Text = elsewhere;
        w.FindControl<TextBox>("TxtOutputFolder")!.Text = Path.Combine(fixture.Root, "out");
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));

        Assert.False(w.FindControl<Button>("BtnPackage")!.IsEnabled);
        Assert.Contains("inside the source folder", w.FindControl<TextBlock>("TxtStatus")!.Text);

        w.FindControl<TextBox>("TxtSetupFile")!.Text = Path.Combine(fixture.Root, "src", "setup.ps1");
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        Assert.True(w.FindControl<Button>("BtnPackage")!.IsEnabled);
    }

    [AvaloniaFact]
    public void Path_rows_show_a_shortened_stand_in_until_the_box_is_focused()
    {
        using var fixture = new FlowFixture();
        var w = new MainWindow();
        w.Show();
        var box = w.FindControl<TextBox>("TxtSetupFile")!;
        var standIn = w.FindControl<TextBlock>("TxtSetupDisplay")!;

        // Empty: the box stays visible so its watermark shows.
        Assert.False(standIn.IsVisible);
        Assert.Equal(1, box.Opacity);

        w.FindControl<TextBox>("TxtSourceFolder")!.Text = Path.Combine(fixture.Root, "src");
        box.Text = Path.Combine(fixture.Root, "src", "setup.ps1");
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        Assert.True(standIn.IsVisible);
        Assert.Equal("setup.ps1", standIn.Text);                      // relative to the source folder
        Assert.Equal(0, box.Opacity);
        Assert.Equal(Path.Combine(fixture.Root, "src", "setup.ps1"), box.Text);   // the real value is untouched

        box.Focus();
        FlowFixture.Pump(TimeSpan.FromMilliseconds(100));
        Assert.False(standIn.IsVisible);
        Assert.Equal(1, box.Opacity);
    }

    /// <summary>Sandboxed settings + a tiny real payload, packaged for real.</summary>
    private sealed class FlowFixture : IDisposable
    {
        private readonly string _root;

        public string Root => _root;

        public FlowFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "wraptune-flow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            Directory.CreateDirectory(Path.Combine(_root, "out"));
            File.WriteAllText(Path.Combine(_root, "src", "setup.ps1"), "Write-Host hi");
            AppSettings.BaseDirOverride = _root;
            new AppSettings().Save();
        }

        public MainWindow RunPackageToCompletion(string? outputName = null)
        {
            var w = new MainWindow();
            w.Show();
            w.FindControl<TextBox>("TxtSourceFolder")!.Text = Path.Combine(_root, "src");
            w.FindControl<TextBox>("TxtSetupFile")!.Text = Path.Combine(_root, "src", "setup.ps1");
            w.FindControl<TextBox>("TxtOutputFolder")!.Text = Path.Combine(_root, "out");
            Pump(TimeSpan.FromMilliseconds(100));
            if (outputName is not null)
            {
                w.FindControl<TextBox>("TxtOutputName")!.Text = outputName;
                Pump(TimeSpan.FromMilliseconds(100));
            }

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
