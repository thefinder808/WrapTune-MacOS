using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Design-review harness, not a test: renders the three flow states to PNGs so
/// they can be compared against the design mocks. Inert unless
/// WRAPTUNE_RENDER_DUMP is set to an output directory:
///   WRAPTUNE_RENDER_DUMP=/tmp/frames dotnet test --filter RenderHarness
/// </summary>
public sealed class RenderHarness
{
    [AvaloniaFact]
    public void Dump_design_states()
    {
        if (Environment.GetEnvironmentVariable("WRAPTUNE_RENDER_DUMP") is not { Length: > 0 } outDir)
            return;
        Directory.CreateDirectory(outDir);

        var sandbox = Path.Combine(Path.GetTempPath(), "wraptune-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        AppSettings.BaseDirOverride = sandbox;
        try
        {
            new AppSettings { Theme = "Midnight" }.Save();   // only Midnight is designed

            // demo payload: a folder with a few files and a nested dir
            var src = Path.Combine(sandbox, "wraptune");
            Directory.CreateDirectory(Path.Combine(src, "assets"));
            File.WriteAllBytes(Path.Combine(src, "Remediate-WindowsPatchHealth.msi"), new byte[24 * 1024 * 1024]);
            File.WriteAllBytes(Path.Combine(src, "assets", "banner.png"), new byte[6 * 1024 * 1024]);
            for (int i = 1; i <= 4; i++)
                File.WriteAllBytes(Path.Combine(src, $"support-{i}.dll"), new byte[2 * 1024 * 1024]);
            var outFolder = Path.Combine(sandbox, "Scratch");
            Directory.CreateDirectory(outFolder);

            var w = new MainWindow();
            w.Show();
            Pump(TimeSpan.FromMilliseconds(700));
            Capture(w, outDir, "state1-empty.png");

            // fill the form + signing (pfx mode) — mock state 2. The pfx must
            // exist or validation keeps the footer in its error state.
            var pfx = Path.Combine(sandbox, "contoso-ov.pfx");
            File.WriteAllBytes(pfx, new byte[64]);
            w.FindControl<TextBox>("TxtSourceFolder")!.Text = src;
            w.FindControl<TextBox>("TxtSetupFile")!.Text = Path.Combine(src, "Remediate-WindowsPatchHealth.msi");
            w.FindControl<TextBox>("TxtOutputFolder")!.Text = outFolder;
            w.FindControl<ToggleSwitch>("ChkSignPayload")!.IsChecked = true;
            w.FindControl<TextBox>("TxtPfxPath")!.Text = pfx;
            w.FindControl<TextBox>("TxtSecret")!.Text = "hunter2hunter2";
            // DispatcherTimers are unreliable under RunJobs pumping, so bypass
            // the debounce and run the derived-info refresh directly.
            var refresh = (Task)typeof(MainWindow)
                .GetMethod("RefreshDerivedAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(w, null)!;
            var refreshDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!refresh.IsCompleted && DateTime.UtcNow < refreshDeadline)
                Pump(TimeSpan.FromMilliseconds(50));
            Pump(TimeSpan.FromMilliseconds(200));
            Capture(w, outDir, "state2-filled.png");

            // real run (signing off so it succeeds) — success state of the run view
            w.FindControl<ToggleSwitch>("ChkSignPayload")!.IsChecked = false;
            Pump(TimeSpan.FromMilliseconds(400));
            var btn = w.FindControl<Button>("BtnPackage")!;
            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline &&
                   w.FindControl<TextBlock>("TxtPercent")!.Text != "Done")
                Pump(TimeSpan.FromMilliseconds(100));
            Capture(w, outDir, "state3-done.png");

            // Daylight sanity frame (derived palette — not part of the mocks).
            new AppSettings { Theme = "Daylight" }.Save();
            var light = new MainWindow();
            light.Show();
            Pump(TimeSpan.FromMilliseconds(700));
            Capture(light, outDir, "state1-light.png");
        }
        finally
        {
            AppSettings.BaseDirOverride = null;
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window w, string dir, string name)
    {
        using var frame = w.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name));
    }
}
