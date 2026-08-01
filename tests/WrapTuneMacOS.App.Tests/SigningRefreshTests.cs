using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Editing a signing input must refresh the footer validation and the Package
/// button immediately. Before the fix, only the three path fields re-ran
/// UpdateFlowUi, so typing the PFX password left the red "password is required"
/// footer and a disabled Package button until an unrelated event (e.g. flipping
/// the Sign toggle) forced a refresh.
/// </summary>
public sealed class SigningRefreshTests : IDisposable
{
    private readonly string _root;

    public SigningRefreshTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wraptune-signrefresh-" + Guid.NewGuid().ToString("N"));
        // Two source folders so each test's setup file is unambiguous for the
        // derived-info auto-detection (which prefers other extensions over .cmd).
        Directory.CreateDirectory(Path.Combine(_root, "src-ps1"));
        Directory.CreateDirectory(Path.Combine(_root, "src-cmd"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        File.WriteAllText(Path.Combine(_root, "src-ps1", "setup.ps1"), "Write-Host hi");
        File.WriteAllText(Path.Combine(_root, "src-cmd", "setup.cmd"), "@echo off");
        // Signing validation only checks the .pfx exists; content is never read.
        File.WriteAllBytes(Path.Combine(_root, "cert.pfx"), [1, 2, 3]);
        AppSettings.BaseDirOverride = _root;
        new AppSettings().Save();
    }

    [AvaloniaFact]
    public void Typing_the_pfx_password_immediately_arms_package()
    {
        var w = NewStagedWindow("src-ps1", "setup.ps1");

        w.FindControl<ToggleSwitch>("ChkSignPayload")!.IsChecked = true;
        w.FindControl<TextBox>("TxtPfxPath")!.Text = Path.Combine(_root, "cert.pfx");
        Pump();
        Assert.False(w.FindControl<Button>("BtnPackage")!.IsEnabled); // password still missing

        w.FindControl<TextBox>("TxtSecret")!.Text = "hunter2";
        Pump();

        Assert.True(w.FindControl<Button>("BtnPackage")!.IsEnabled);
    }

    [AvaloniaFact]
    public void Toggling_sign_all_files_immediately_updates_validation()
    {
        // A .cmd setup can't be Authenticode-signed, so with signing armed the
        // form is blocked until "sign all signable files" is switched on.
        var w = NewStagedWindow("src-cmd", "setup.cmd");

        w.FindControl<ToggleSwitch>("ChkSignPayload")!.IsChecked = true;
        w.FindControl<TextBox>("TxtPfxPath")!.Text = Path.Combine(_root, "cert.pfx");
        w.FindControl<TextBox>("TxtSecret")!.Text = "hunter2";
        Pump();
        Assert.False(w.FindControl<Button>("BtnPackage")!.IsEnabled); // blocked: unsignable setup

        w.FindControl<ToggleSwitch>("ChkSignAllFiles")!.IsChecked = true;
        Pump();

        Assert.True(w.FindControl<Button>("BtnPackage")!.IsEnabled);
    }

    /// <summary>Window with the three paths staged and the derived-info debounce settled.</summary>
    private MainWindow NewStagedWindow(string srcDir, string setupName)
    {
        var w = new MainWindow();
        w.Show();
        w.FindControl<TextBox>("TxtSourceFolder")!.Text = Path.Combine(_root, srcDir);
        w.FindControl<TextBox>("TxtSetupFile")!.Text = Path.Combine(_root, srcDir, setupName);
        w.FindControl<TextBox>("TxtOutputFolder")!.Text = Path.Combine(_root, "out");
        Pump(TimeSpan.FromMilliseconds(400)); // outlast the 250 ms derived-info debounce
        return w;
    }

    private static void Pump() => Pump(TimeSpan.FromMilliseconds(100));

    private static void Pump(TimeSpan duration)
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
