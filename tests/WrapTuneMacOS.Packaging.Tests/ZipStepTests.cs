using System.IO.Compression;
using System.Security.Cryptography;

namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// The payload-zip step and the output file's name: what goes into the payload
/// (and what is deliberately left out), progress and cancellation while
/// zipping, and naming the output so packages don't replace each other.
/// </summary>
public sealed class ZipStepTests
{
    /// <summary>Synchronous collector — Progress&lt;T&gt; posts to the thread pool,
    /// so lines could arrive after the assertion.</summary>
    private sealed class Lines(Action<string>? onLine = null) : IProgress<string>
    {
        public List<string> All { get; } = [];
        public void Report(string value)
        {
            lock (All) All.Add(value);
            onLine?.Invoke(value);
        }
    }

    /// <summary>Every entry name in the payload, directory markers included.</summary>
    private static List<string> PayloadEntryNames(string package)
    {
        using var ms = new MemoryStream(IntuneWinReader.Read(package).DecryptedZip);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private static PackageRequest Request(TestWorkspace ws, string setup = "setup.exe", string? outputName = null) =>
        new(ws.Source, Path.Combine(ws.Source, setup), ws.Output, Overwrite: true, outputName);

    // ── What goes into the payload ──────────────────────────────────────────

    [Fact]
    public async Task Mac_metadata_files_are_left_out_of_the_payload()
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", "installer");
        ws.AddSourceFile("data/readme.txt", "docs");
        ws.AddSourceFile(".DS_Store", "finder junk");
        ws.AddSourceFile("data/.DS_Store", "finder junk");
        ws.AddSourceFile("data/._readme.txt", "appledouble junk");
        var log = new Lines();

        var result = await new IntuneWinWriter().PackageAsync(Request(ws), log);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["data/readme.txt", "setup.exe"], PayloadEntryNames(result.OutputPath!));
        Assert.Contains("Left out 3 macOS metadata files (.DS_Store, ._*).", log.All);
        Assert.True(IntuneWinReader.Read(result.OutputPath!).IsValid);
    }

    [Fact]
    public async Task Ordinary_dotfiles_are_still_packaged()
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", "installer");
        ws.AddSourceFile(".config", "kept");
        ws.AddSourceFile("_underscore.txt", "kept");
        var log = new Lines();

        var result = await new IntuneWinWriter().PackageAsync(Request(ws), log);

        Assert.True(result.Success, result.Error);
        Assert.Equal([".config", "_underscore.txt", "setup.exe"], PayloadEntryNames(result.OutputPath!));
        Assert.DoesNotContain(log.All, l => l.StartsWith("Left out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_folders_survive_including_ones_that_held_only_metadata()
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", "installer");
        Directory.CreateDirectory(Path.Combine(ws.Source, "empty"));
        ws.AddSourceFile("only-junk/.DS_Store", "finder junk");
        ws.AddSourceFile("nested/inner/file.txt", "x");

        var result = await new IntuneWinWriter().PackageAsync(Request(ws));

        Assert.True(result.Success, result.Error);
        Assert.Equal(
            ["empty/", "nested/inner/file.txt", "only-junk/", "setup.exe"],
            PayloadEntryNames(result.OutputPath!));
    }

    [Fact]
    public async Task Rebuilding_into_the_source_folder_does_not_wrap_the_previous_package()
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", RandomNumberGenerator.GetBytes(64 * 1024));
        // Output folder == source folder, as people do with the official tool.
        var request = new PackageRequest(ws.Source, Path.Combine(ws.Source, "setup.exe"), ws.Source, Overwrite: true);
        var writer = new IntuneWinWriter();

        var first = await writer.PackageAsync(request);
        Assert.True(first.Success, first.Error);
        var firstSize = new FileInfo(first.OutputPath!).Length;

        var log = new Lines();
        var second = await writer.PackageAsync(request, log);
        Assert.True(second.Success, second.Error);

        Assert.Equal(["setup.exe"], PayloadEntryNames(second.OutputPath!));
        Assert.Contains("Left out the previous output package found inside the source folder.", log.All);
        // Random bytes don't compress, so a wrapped copy would roughly double this.
        Assert.InRange(new FileInfo(second.OutputPath!).Length, firstSize - 1024, firstSize + 1024);
    }

    [Fact]
    public async Task Other_intunewin_files_in_the_source_are_ordinary_payload()
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", "installer");
        ws.AddSourceFile("dependency.intunewin", "someone put this here on purpose");

        var result = await new IntuneWinWriter().PackageAsync(
            new PackageRequest(ws.Source, Path.Combine(ws.Source, "setup.exe"), ws.Source, Overwrite: true));

        Assert.True(result.Success, result.Error);
        Assert.Equal(["dependency.intunewin", "setup.exe"], PayloadEntryNames(result.OutputPath!));
    }

    [Fact]
    public async Task File_times_outside_the_zip_range_do_not_fail_the_package()
    {
        using var ws = new TestWorkspace();
        var setup = ws.AddSourceFile("setup.exe", "installer");
        File.SetLastWriteTimeUtc(setup, new DateTime(1975, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await new IntuneWinWriter().PackageAsync(Request(ws));

        Assert.True(result.Success, result.Error);
        Assert.Equal(["setup.exe"], PayloadEntryNames(result.OutputPath!));
    }

    // ── Progress and cancellation ───────────────────────────────────────────

    [Fact]
    public async Task Zipping_reports_percent_progress()
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", RandomNumberGenerator.GetBytes(6 * 1024 * 1024));
        var log = new Lines();

        var result = await new IntuneWinWriter().PackageAsync(Request(ws), log);

        Assert.True(result.Success, result.Error);
        var zipLines = log.All.Where(l => l.StartsWith("Zipping… ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(zipLines);
        Assert.All(zipLines, l => Assert.EndsWith("%", l));
        // The stage header still comes first, so the UI's stage mapping holds.
        Assert.True(log.All.IndexOf("Zipping payload…") < log.All.IndexOf(zipLines[0]));
    }

    [Fact]
    public async Task Cancelling_while_zipping_stops_mid_file_and_leaves_no_output()
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", RandomNumberGenerator.GetBytes(6 * 1024 * 1024));
        using var cts = new CancellationTokenSource();
        var log = new Lines(line =>
        {
            if (line.StartsWith("Zipping… ", StringComparison.Ordinal)) cts.Cancel();
        });

        var result = await new IntuneWinWriter().PackageAsync(Request(ws), log, cts.Token);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(log.All, l => l.StartsWith("Encrypting", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(ws.Output));
    }

    // ── Output name ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Output_is_named_after_the_setup_file_by_default()
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("Invoke-AppDeployToolkit.exe", "launcher");

        var result = await new IntuneWinWriter().PackageAsync(Request(ws, "Invoke-AppDeployToolkit.exe"));

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(ws.Output, "Invoke-AppDeployToolkit.intunewin"), result.OutputPath);
    }

    [Theory]
    [InlineData("Zoom 6.1", "Zoom 6.1.intunewin")]
    [InlineData("  Zoom 6.1  ", "Zoom 6.1.intunewin")]
    [InlineData("Zoom 6.1.intunewin", "Zoom 6.1.intunewin")]
    [InlineData("Zoom.INTUNEWIN", "Zoom.intunewin")]
    public async Task A_requested_output_name_is_used(string requested, string expectedFile)
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("Invoke-AppDeployToolkit.exe", "launcher");

        var result = await new IntuneWinWriter().PackageAsync(
            Request(ws, "Invoke-AppDeployToolkit.exe", requested));

        Assert.True(result.Success, result.Error);
        Assert.Equal(Path.Combine(ws.Output, expectedFile), result.OutputPath);

        // Only the file's name changes — the metadata Intune reads still names the setup file.
        var contents = IntuneWinReader.Read(result.OutputPath!);
        Assert.True(contents.IsValid);
        Assert.Equal("Invoke-AppDeployToolkit.exe", contents.Detection.Name);
        Assert.Equal("Invoke-AppDeployToolkit.exe", contents.Detection.SetupFile);
    }

    [Fact]
    public async Task Two_packages_with_the_same_setup_name_can_share_an_output_folder()
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("Invoke-AppDeployToolkit.exe", "launcher");
        var writer = new IntuneWinWriter();

        var zoom = await writer.PackageAsync(Request(ws, "Invoke-AppDeployToolkit.exe", "Zoom"));
        var chrome = await writer.PackageAsync(Request(ws, "Invoke-AppDeployToolkit.exe", "Chrome"));

        Assert.True(zoom.Success && chrome.Success);
        Assert.True(File.Exists(zoom.OutputPath));
        Assert.True(File.Exists(chrome.OutputPath));
        Assert.NotEqual(zoom.OutputPath, chrome.OutputPath);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/name")]
    [InlineData("sub\\name")]
    [InlineData("..")]
    [InlineData(".intunewin")]
    [InlineData("what?")]
    [InlineData("a:b")]
    public async Task Unusable_output_names_are_rejected_before_anything_is_written(string name)
    {
        using var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", "installer");

        var result = await new IntuneWinWriter().PackageAsync(Request(ws, outputName: name));

        Assert.False(result.Success);
        Assert.Contains("Output name", result.Error!, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(ws.Root, "*.intunewin", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Zoom 6.1")]
    [InlineData("Zoom.intunewin")]
    public void Blank_and_ordinary_output_names_are_accepted(string? name)
        => Assert.Null(IntuneWinWriter.OutputNameError(name));

    [Theory]
    [InlineData(".DS_Store", true)]
    [InlineData("/some/folder/.DS_Store", true)]
    [InlineData("._setup.exe", true)]
    [InlineData("setup.exe", false)]
    [InlineData(".config", false)]
    [InlineData("_.txt", false)]
    public void Mac_metadata_files_are_recognized(string path, bool expected)
        => Assert.Equal(expected, IntuneWinWriter.IsMacMetadataFile(path));
}
