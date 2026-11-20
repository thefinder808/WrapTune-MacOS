using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WrapTuneMacOS.Packaging;
using WrapTuneMacOS.Services;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Batch packaging: planning a queue from dropped folders (pure logic), and the
/// queue driven through the real window and the real engine.
/// </summary>
public sealed class BatchQueueTests : IDisposable
{
    private readonly string _root;

    public BatchQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wraptune-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        AppSettings.BaseDirOverride = _root;
        new AppSettings().Save();
    }

    public void Dispose()
    {
        AppSettings.BaseDirOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Creates a source folder under the sandbox holding the named files.</summary>
    private string Folder(string relative, params string[] files)
    {
        var folder = Path.Combine(_root, relative);
        Directory.CreateDirectory(folder);
        foreach (var file in files)
            File.WriteAllText(Path.Combine(folder, file), "payload for " + file);
        return folder;
    }

    // ── Planning ────────────────────────────────────────────────────────────

    [Fact]
    public void Each_folder_becomes_a_package_named_after_it_with_its_setup_file_guessed()
    {
        var zoom = Folder("Zoom 6.1", "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.ps1");
        var chrome = Folder("Chrome 130", "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.ps1");
        var unclear = Folder("Unclear", "a.msi", "b.exe");

        var items = BatchQueue.Plan([zoom, chrome + Path.DirectorySeparatorChar, unclear]);

        Assert.Equal(["Zoom 6.1", "Chrome 130", "Unclear"], items.Select(i => i.OutputName));
        Assert.Equal(Path.Combine(zoom, "Invoke-AppDeployToolkit.exe"), items[0].SetupFile);
        Assert.Equal(chrome, items[1].Folder);                 // trailing separator normalized away
        Assert.Null(items[2].SetupFile);                       // ambiguous — the user must choose
        Assert.Null(BatchQueue.Problem(items[0]));
        Assert.Equal("choose a setup file", BatchQueue.Problem(items[2]));
    }

    [Fact]
    public void Same_named_folders_from_different_places_get_distinct_package_names()
    {
        var first = Folder(Path.Combine("vendor-a", "Setup"), "setup.msi");
        var second = Folder(Path.Combine("vendor-b", "Setup"), "setup.msi");
        var third = Folder(Path.Combine("vendor-c", "setup"), "setup.msi");   // differs only in case

        var items = BatchQueue.Plan([first, second, third]);

        Assert.Equal(["Setup", "Setup (2)", "setup (3)"], items.Select(i => i.OutputName));
    }

    [Fact]
    public void Files_missing_folders_and_repeats_are_left_out_of_the_queue()
    {
        var zoom = Folder("Zoom", "setup.msi");
        var chrome = Folder("Chrome", "setup.msi");
        var existing = BatchQueue.Plan([zoom]);

        var added = BatchQueue.Plan(
            [zoom, chrome, chrome, Path.Combine(zoom, "setup.msi"), Path.Combine(_root, "nope"), "  "], existing);

        Assert.Equal(["Chrome"], added.Select(i => i.OutputName));
    }

    [Fact]
    public void A_setup_file_that_vanished_or_left_the_folder_is_a_problem()
    {
        var zoom = Folder("Zoom", "setup.msi");
        var elsewhere = Folder("Elsewhere", "other.msi");
        var item = BatchQueue.Plan([zoom]).Single();

        item.SetupFile = Path.Combine(elsewhere, "other.msi");
        Assert.Equal("setup file is outside the folder", BatchQueue.Problem(item));

        item.SetupFile = Path.Combine(zoom, "deleted.msi");
        Assert.Equal("setup file is missing", BatchQueue.Problem(item));
    }

    [Fact]
    public void Result_wording_counts_what_was_created_failed_and_never_ran()
    {
        var item = new QueueItem { Folder = _root, OutputName = "x" };
        QueueResult Ok() => new(item, Path.Combine(_root, "x.intunewin"), null);
        QueueResult Failed() => new(item, null, "boom");
        QueueResult NotRun() => new(item, null, null);

        Assert.Equal("Packaged 2", BatchQueue.ResultTitle([Ok(), Ok()]));
        Assert.Equal("done — 2 packages created", BatchQueue.ResultStatus([Ok(), Ok()]));
        Assert.Equal("done — 1 package created", BatchQueue.ResultStatus([Ok()]));

        Assert.Equal("Packaged 1 of 3", BatchQueue.ResultTitle([Ok(), Failed(), NotRun()]));
        Assert.Equal("1 created · 1 failed · 1 not run", BatchQueue.ResultStatus([Ok(), Failed(), NotRun()]));

        Assert.Equal("Nothing packaged", BatchQueue.ResultTitle([Failed()]));
        Assert.True(NotRun().Skipped);
        Assert.False(Failed().Skipped);
    }

    [Theory]
    [InlineData(1, false, "ready — 1 package")]
    [InlineData(3, false, "ready — 3 packages")]
    [InlineData(3, true, "ready — sign, then wrap 3 packages")]
    public void Ready_status_counts_the_packages(int count, bool signing, string expected)
        => Assert.Equal(expected, BatchQueue.ReadyStatus(count, signing));

    // ── Through the window ──────────────────────────────────────────────────

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

    private static void PumpUntil(Func<bool> done, int seconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline && !done())
            Pump(TimeSpan.FromMilliseconds(50));
    }

    private MainWindow QueueWindow(params string[] folders)
    {
        var w = new MainWindow();
        w.Show();
        w.AddToQueue(folders);
        w.FindControl<TextBox>("TxtOutputFolder")!.Text = Path.Combine(_root, "out");
        Pump(TimeSpan.FromMilliseconds(100));
        return w;
    }

    private static void Click(MainWindow w, string button) =>
        w.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Several_folders_switch_the_form_to_a_queue_and_clearing_switches_back()
    {
        var w = QueueWindow(Folder("Zoom", "setup.msi"), Folder("Chrome", "setup.msi"));

        Assert.Equal(2, w.Queue.Count);
        Assert.True(w.FindControl<StackPanel>("QueueList")!.IsVisible);
        Assert.Equal(2, w.FindControl<StackPanel>("QueueList")!.Children.Count);
        Assert.Equal("Queue", w.FindControl<TextBlock>("TxtStep1Title")!.Text);
        // The single-package rows step aside; output folder and overwrite stay, shared.
        Assert.False(w.FindControl<Border>("RowSource")!.IsVisible);
        Assert.False(w.FindControl<Border>("RowSetup")!.IsVisible);
        Assert.False(w.FindControl<Border>("RowOutputName")!.IsVisible);
        Assert.True(w.FindControl<Border>("RowOutput")!.IsVisible);
        Assert.False(w.FindControl<Panel>("DropZone")!.IsVisible);
        Assert.True(w.FindControl<Button>("BtnPackage")!.IsEnabled);
        Assert.Equal("ready — 2 packages", w.FindControl<TextBlock>("TxtStatus")!.Text);

        Click(w, "BtnClearQueue");
        Pump(TimeSpan.FromMilliseconds(100));
        Assert.Empty(w.Queue);
        Assert.False(w.FindControl<StackPanel>("QueueList")!.IsVisible);
        Assert.Equal("Files", w.FindControl<TextBlock>("TxtStep1Title")!.Text);
        Assert.True(w.FindControl<Border>("RowSource")!.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void One_folder_with_no_queue_showing_is_just_the_source_folder()
    {
        var zoom = Folder("Zoom", "setup.msi");
        var w = new MainWindow();
        w.Show();

        w.AddToQueue([zoom]);
        Pump(TimeSpan.FromMilliseconds(100));

        Assert.Empty(w.Queue);
        Assert.Equal(zoom, w.FindControl<TextBox>("TxtSourceFolder")!.Text);
        Assert.Equal(Path.Combine(zoom, "setup.msi"), w.FindControl<TextBox>("TxtSetupFile")!.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void A_folder_whose_setup_file_is_unclear_blocks_the_batch_until_one_is_chosen()
    {
        var unclear = Folder("Unclear", "a.msi", "b.exe");
        var w = QueueWindow(Folder("Zoom", "setup.msi"), unclear);

        Assert.False(w.FindControl<Button>("BtnPackage")!.IsEnabled);
        Assert.Equal("Unclear: choose a setup file", w.FindControl<TextBlock>("TxtStatus")!.Text);

        w.SetQueueSetup(w.Queue[1], Path.Combine(unclear, "a.msi"));
        Pump(TimeSpan.FromMilliseconds(100));
        Assert.True(w.FindControl<Button>("BtnPackage")!.IsEnabled);
        w.Close();
    }

    [AvaloniaFact]
    public void The_batch_packages_every_folder_under_its_own_name()
    {
        // Both setup files share a name — the case that used to make one
        // package replace the other.
        var w = QueueWindow(
            Folder("Zoom 6.1", "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.ps1"),
            Folder("Chrome 130", "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.ps1"));

        Click(w, "BtnPackage");
        PumpUntil(() => w.FindControl<Border>("QueueResultCard")!.IsVisible);

        Assert.Equal("Packaged 2", w.FindControl<TextBlock>("TxtRunTitle")!.Text);
        foreach (var name in new[] { "Zoom 6.1.intunewin", "Chrome 130.intunewin" })
        {
            var package = Path.Combine(_root, "out", name);
            Assert.True(File.Exists(package), name);
            var contents = IntuneWinReader.Read(package);
            Assert.True(contents.IsValid);
            Assert.Equal("Invoke-AppDeployToolkit.exe", contents.Detection.SetupFile);
        }

        // A summary line plus one row per package; the single-run views step aside.
        Assert.Equal(3, w.FindControl<StackPanel>("QueueResultList")!.Children.Count);
        Assert.False(w.FindControl<Border>("ReceiptCard")!.IsVisible);
        Assert.False(w.FindControl<Border>("StageCard")!.IsVisible);
        Assert.True(w.FindControl<Button>("BtnOpenOutput")!.IsVisible);
        Assert.True(w.FindControl<Button>("BtnNewPackage")!.IsVisible);
        Assert.False(w.FindControl<Button>("BtnPackage")!.IsVisible);
        Assert.Equal("done — 2 packages created", w.FindControl<TextBlock>("TxtStatus")!.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void One_failure_does_not_stop_the_rest_of_the_batch()
    {
        var w = QueueWindow(Folder("Zoom", "setup.ps1"), Folder("Chrome", "setup.ps1"));
        // Zoom's package already exists and overwrite is off → that item fails.
        File.WriteAllText(Path.Combine(_root, "out", "Zoom.intunewin"), "already here");
        w.FindControl<ToggleSwitch>("ChkOverwrite")!.IsChecked = false;
        Pump(TimeSpan.FromMilliseconds(100));

        Click(w, "BtnPackage");
        PumpUntil(() => w.FindControl<Border>("QueueResultCard")!.IsVisible);

        Assert.Equal("Packaged 1 of 2", w.FindControl<TextBlock>("TxtRunTitle")!.Text);
        Assert.Equal("1 created · 1 failed", w.FindControl<TextBlock>("TxtStatus")!.Text);
        Assert.True(IntuneWinReader.Read(Path.Combine(_root, "out", "Chrome.intunewin")).IsValid);
        Assert.Equal("already here", File.ReadAllText(Path.Combine(_root, "out", "Zoom.intunewin")));

        // Back to the form: the queue is still there to fix and re-run.
        Click(w, "BtnNewPackage");
        Pump(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, w.Queue.Count);
        Assert.True(w.FindControl<StackPanel>("QueueList")!.IsVisible);
        Assert.True(w.FindControl<Button>("BtnPackage")!.IsVisible);
        w.Close();
    }
}
