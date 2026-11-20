using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WrapTuneMacOS.Services;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Design-review harness, not a test: renders the flow states (plus the failed
/// run, the inspector, and the Daylight theme) to PNGs so they can be compared
/// against the design mocks. Inert unless
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

            // demo payload: a folder with a few files and a nested dir. Random
            // (incompressible) bytes so the zip stage takes long enough for the
            // mid-run capture to actually catch the run in flight.
            var src = Path.Combine(sandbox, "wraptune");
            Directory.CreateDirectory(Path.Combine(src, "assets"));
            var big = new byte[24 * 1024 * 1024];
            Random.Shared.NextBytes(big);
            File.WriteAllBytes(Path.Combine(src, "Remediate-WindowsPatchHealth.msi"), big);
            var medium = new byte[6 * 1024 * 1024];
            Random.Shared.NextBytes(medium);
            File.WriteAllBytes(Path.Combine(src, "assets", "banner.png"), medium);
            for (int i = 1; i <= 4; i++)
            {
                var dll = new byte[2 * 1024 * 1024];
                Random.Shared.NextBytes(dll);
                File.WriteAllBytes(Path.Combine(src, $"support-{i}.dll"), dll);
            }
            var outFolder = Path.Combine(sandbox, "Scratch");
            Directory.CreateDirectory(outFolder);

            var w = new MainWindow();
            w.Show();
            Pump(TimeSpan.FromMilliseconds(700));
            Capture(w, outDir, "state1-empty.png");

            // A folder dragged over the empty form: the source row and the drop
            // zone light up and the footer says what letting go will do.
            w.ShowDropTarget(DropKind.SourceFolder);
            Pump(TimeSpan.FromMilliseconds(200));
            Capture(w, outDir, "state1-dragover.png");
            w.ShowDropTarget(null);
            Pump(TimeSpan.FromMilliseconds(100));

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

            // Keyboard-focus frames: a path box, a segment, and a button.
            w.FindControl<TextBox>("TxtSourceFolder")!.Focus(Avalonia.Input.NavigationMethod.Tab);
            Pump(TimeSpan.FromMilliseconds(200));
            Capture(w, outDir, "focus-path.png");
            w.FindControl<RadioButton>("RbPkcs11")!.Focus(Avalonia.Input.NavigationMethod.Tab);
            Pump(TimeSpan.FromMilliseconds(200));
            Capture(w, outDir, "focus-segment.png");
            w.FindControl<ToggleSwitch>("ChkOverwrite")!.Focus(Avalonia.Input.NavigationMethod.Tab);
            Pump(TimeSpan.FromMilliseconds(200));
            Capture(w, outDir, "focus-switch.png");
            w.FindControl<Button>("BtnBrowseSetup")!.Focus(Avalonia.Input.NavigationMethod.Tab);
            Pump(TimeSpan.FromMilliseconds(200));
            Capture(w, outDir, "focus-button.png");
            btnFocusReset(w);

            // real run (signing off so it succeeds) — success state of the run view
            w.FindControl<ToggleSwitch>("ChkSignPayload")!.IsChecked = false;
            Pump(TimeSpan.FromMilliseconds(400));
            var btn = w.FindControl<Button>("BtnPackage")!;
            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(120));   // catch the run mid-flight
            Capture(w, outDir, "state3-running.png");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline &&
                   w.FindControl<TextBlock>("TxtPercent")!.Text != "Done")
                Pump(TimeSpan.FromMilliseconds(100));
            // One more pump so a render pass runs after the final state change —
            // CaptureRenderedFrame otherwise returns the last (stale) frame.
            Pump(TimeSpan.FromMilliseconds(300));
            Capture(w, outDir, "state3-done.png");

            // The inspector, on the package that run just produced.
            var package = Directory.EnumerateFiles(outFolder, "*.intunewin").First();
            var inspector = new InspectWindow(package);
            inspector.Show();
            var inspectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < inspectDeadline &&
                   inspector.FindControl<Button>("BtnExtract")!.IsEnabled == false)
                Pump(TimeSpan.FromMilliseconds(100));
            Pump(TimeSpan.FromMilliseconds(300));
            Capture(inspector, outDir, "inspect.png");
            inspector.Close();

            // Failed run: same package again with overwrite off.
            w.FindControl<Button>("BtnNewPackage")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(200));
            w.FindControl<ToggleSwitch>("ChkOverwrite")!.IsChecked = false;
            Pump(TimeSpan.FromMilliseconds(200));
            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var failDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < failDeadline &&
                   w.FindControl<TextBlock>("TxtStageName")!.Text != "Failed")
                Pump(TimeSpan.FromMilliseconds(100));
            Pump(TimeSpan.FromMilliseconds(300));
            Capture(w, outDir, "state3-failed.png");
            w.Close();

            // Batch queue: three folders, one of them needing a setup file chosen;
            // then the results of a run where one item fails.
            var apps = Path.Combine(sandbox, "Apps");
            foreach (var (app, files) in new[]
                     {
                         ("Zoom 6.1", new[] { "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.ps1" }),
                         ("Chrome 130", new[] { "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.ps1" }),
                         ("7-Zip 24", new[] { "7z-x64.msi", "prereq.exe" }),
                     })
            {
                Directory.CreateDirectory(Path.Combine(apps, app));
                foreach (var f in files)
                {
                    var bytes = new byte[512 * 1024];
                    Random.Shared.NextBytes(bytes);
                    File.WriteAllBytes(Path.Combine(apps, app, f), bytes);
                }
            }
            new AppSettings { Theme = "Midnight", ThemeChosen = true }.Save();
            var q = new MainWindow();
            q.Show();
            q.AddToQueue(Directory.GetDirectories(apps).OrderByDescending(d => d));
            q.FindControl<TextBox>("TxtOutputFolder")!.Text = outFolder;
            Pump(TimeSpan.FromMilliseconds(500));
            Capture(q, outDir, "queue-form.png");

            var sevenZip = q.Queue.First(i => i.FolderName == "7-Zip 24");
            q.SetQueueSetup(sevenZip, Path.Combine(sevenZip.Folder, "7z-x64.msi"));
            File.WriteAllText(Path.Combine(outFolder, "Chrome 130.intunewin"), "already here");
            q.FindControl<ToggleSwitch>("ChkOverwrite")!.IsChecked = false;
            Pump(TimeSpan.FromMilliseconds(300));
            q.FindControl<Button>("BtnPackage")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var queueDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < queueDeadline &&
                   !q.FindControl<Border>("QueueResultCard")!.IsVisible)
                Pump(TimeSpan.FromMilliseconds(100));
            Pump(TimeSpan.FromMilliseconds(300));
            Capture(q, outDir, "queue-done.png");
            q.Close();

            // Daylight frames (derived palette — not part of the mocks).
            new AppSettings { Theme = "Daylight", ThemeChosen = true }.Save();
            var light = new MainWindow();
            light.Show();
            Pump(TimeSpan.FromMilliseconds(700));
            Capture(light, outDir, "state1-light.png");
            light.FindControl<TextBox>("TxtSourceFolder")!.Text = src;
            light.FindControl<TextBox>("TxtSetupFile")!.Text = Path.Combine(src, "Remediate-WindowsPatchHealth.msi");
            light.FindControl<TextBox>("TxtOutputFolder")!.Text = outFolder;
            light.FindControl<ToggleSwitch>("ChkSignPayload")!.IsChecked = true;
            Pump(TimeSpan.FromMilliseconds(500));
            Capture(light, outDir, "state2-light.png");
        }
        finally
        {
            AppSettings.BaseDirOverride = null;
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Drop keyboard focus so later frames aren't drawn with a ring.</summary>
    private static void btnFocusReset(Window w)
    {
        w.FocusManager?.ClearFocus();
        Pump(TimeSpan.FromMilliseconds(100));
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
        // A state change needs a layout pass and then a composition pass before
        // it is in the frame; one forced tick isn't always both.
        for (int i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        using var frame = w.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name));
    }
}
