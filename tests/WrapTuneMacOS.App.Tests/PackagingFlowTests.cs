using WrapTuneMacOS.Packaging;
using WrapTuneMacOS.Services;
using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Pure logic behind the redesigned flow window: engine progress-line → stage
/// mapping, in-stage percentages, and the derived strings the design shows.
/// </summary>
public sealed class PackagingFlowTests
{
    [Theory]
    [InlineData("Zipping payload…", PackageStage.Zip)]
    [InlineData("Zipping… 40%", PackageStage.Zip)]
    [InlineData("Encrypting (AES-256-CBC) and computing HMAC-SHA256…", PackageStage.Encrypt)]
    [InlineData("Encrypting… 30%", PackageStage.Encrypt)]
    [InlineData("Reading MSI metadata…", PackageStage.MsiMetadata)]
    [InlineData("MSI: {123} 1.0.0", PackageStage.MsiMetadata)]
    [InlineData("Writing Detection.xml…", PackageStage.DetectionXml)]
    [InlineData("Assembling package…", PackageStage.Assemble)]
    [InlineData("Created /tmp/out/setup.intunewin", PackageStage.Assemble)]
    public void Engine_progress_lines_map_to_their_stage(string line, PackageStage expected)
        => Assert.Equal(expected, PackagingFlow.StageFor(line));

    [Fact]
    public void Unrecognized_lines_stay_in_the_raw_log_only()
    {
        Assert.Null(PackagingFlow.StageFor("WARNING  The temp volume has 12 MB free"));
        Assert.Null(PackagingFlow.StageFor("Signed 3 files."));   // signing is app-driven, not line-driven
    }

    [Theory]
    [InlineData("Encrypting… 30%", 30)]
    [InlineData("Encrypting… 90%", 90)]
    [InlineData("Encrypting (AES-256-CBC) and computing HMAC-SHA256…", null)]
    [InlineData("Zipping payload…", null)]
    public void Encrypt_percent_is_parsed_from_chunk_progress_lines(string line, int? expected)
        => Assert.Equal(expected, PackagingFlow.EncryptPercent(line));

    [Theory]
    [InlineData("Zipping… 40%", 40)]
    [InlineData("Zipping payload…", null)]
    [InlineData("Encrypting… 30%", null)]
    public void Zip_percent_is_parsed_from_chunk_progress_lines(string line, int? expected)
        => Assert.Equal(expected, PackagingFlow.ZipPercent(line));

    [Theory]
    [InlineData("Zoom 6.1", "setup.msi", "setup")]                       // official-tool naming
    [InlineData("Zoom 6.1", "Invoke-AppDeployToolkit.exe", "Zoom 6.1")]  // PSADT v4 launcher
    [InlineData("Zoom 6.1", "Deploy-Application.exe", "Zoom 6.1")]       // PSADT v3 launcher
    [InlineData("Zoom 6.1", "deploy-application.EXE", "Zoom 6.1")]
    [InlineData(null, "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit")]
    [InlineData("Zoom 6.1", null, "")]
    [InlineData("Zoom 6.1", "  ", "")]
    public void Default_output_name_is_the_setup_name_or_the_folder_for_psadt(
        string? folder, string? setup, string expected)
    {
        var source = folder is null ? null : Path.Combine(Path.GetTempPath(), folder) + Path.DirectorySeparatorChar;
        var setupPath = string.IsNullOrWhiteSpace(setup) || source is null ? setup : Path.Combine(source, setup);
        Assert.Equal(expected, PackagingFlow.DefaultOutputName(source, setupPath));
    }

    [Fact]
    public void Home_folder_is_shortened_to_a_tilde()
    {
        var sep = Path.DirectorySeparatorChar;
        var home = $"{sep}Users{sep}sam";

        Assert.Equal($"~{sep}Packages{sep}Zoom", PackagingFlow.AbbreviateHome($"{home}{sep}Packages{sep}Zoom", home));
        Assert.Equal("~", PackagingFlow.AbbreviateHome(home, home));
        Assert.Equal($"~{sep}x", PackagingFlow.AbbreviateHome($"{home}{sep}x", home + sep));   // trailing slash on home
        // A sibling whose name merely starts the same is not inside home.
        Assert.Equal($"{sep}Users{sep}samantha{sep}x", PackagingFlow.AbbreviateHome($"{sep}Users{sep}samantha{sep}x", home));
        Assert.Equal($"{sep}Volumes{sep}Share", PackagingFlow.AbbreviateHome($"{sep}Volumes{sep}Share", home));
        Assert.Equal($"{sep}Volumes{sep}Share", PackagingFlow.AbbreviateHome($"{sep}Volumes{sep}Share", null));
        Assert.Equal("", PackagingFlow.AbbreviateHome(null, home));
    }

    [Fact]
    public void Setup_file_is_shown_relative_to_the_source_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "wraptune-display");
        var source = Path.Combine(root, "Zoom 6.1");

        Assert.Equal("setup.msi", PackagingFlow.SetupDisplay(source, Path.Combine(source, "setup.msi"), null));
        Assert.Equal(Path.Combine("Files", "setup.msi"),
            PackagingFlow.SetupDisplay(source + Path.DirectorySeparatorChar, Path.Combine(source, "Files", "setup.msi"), null));

        // Outside the source folder: the full path, so the mistake is visible.
        var outside = Path.Combine(root, "elsewhere", "setup.msi");
        Assert.Equal(outside, PackagingFlow.SetupDisplay(source, outside, null));
        Assert.Equal(outside, PackagingFlow.SetupDisplay(null, outside, null));
        Assert.Equal("", PackagingFlow.SetupDisplay(source, "  ", null));
    }

    /// <summary>A throwaway folder holding the named (empty) files.</summary>
    private static string FolderWith(params string[] relativeFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), "wraptune-guess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var relative in relativeFiles)
        {
            var full = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "");
        }
        return root;
    }

    [Theory]
    [InlineData("setup.msi", new[] { "setup.msi", "readme.txt" })]                       // the only installer
    [InlineData("Invoke-AppDeployToolkit.exe",                                           // PSADT v4: exe + ps1 + helpers
        new[] { "Invoke-AppDeployToolkit.exe", "Invoke-AppDeployToolkit.ps1", "Files/setup.msi" })]
    [InlineData("Deploy-Application.exe",                                                // PSADT v3
        new[] { "Deploy-Application.exe", "Deploy-Application.ps1", "ServiceUI.exe" })]
    [InlineData(null, new[] { "setup.msi", "prereq.exe" })]                              // genuinely ambiguous
    [InlineData(null, new[] { "readme.txt", "Files/setup.msi" })]                        // nothing at the top level
    [InlineData("setup.exe", new[] { "setup.exe", "._setup.exe", ".DS_Store" })]         // mac metadata isn't a candidate
    public void Setup_file_is_guessed_only_when_the_choice_is_obvious(string? expected, string[] files)
    {
        var folder = FolderWith(files);
        try
        {
            var guess = PackagingFlow.GuessSetupFile(folder);
            Assert.Equal(expected is null ? null : Path.Combine(folder, expected), guess);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Setup_guess_is_null_without_a_real_folder()
    {
        Assert.Null(PackagingFlow.GuessSetupFile(null));
        Assert.Null(PackagingFlow.GuessSetupFile("  "));
        Assert.Null(PackagingFlow.GuessSetupFile(Path.Combine(Path.GetTempPath(), "wraptune-does-not-exist")));
    }

    [Fact]
    public void Installers_are_listed_nearest_the_top_first_and_capped()
    {
        var folder = FolderWith(
            "Files/deep/zeta.exe", "b_setup.msi", "a_setup.exe", "Files/inner.msi",
            "notes.txt", "._a_setup.exe", "Deploy.ps1");
        try
        {
            var all = PackagingFlow.FindInstallers(folder);
            Assert.Equal(
                [
                    "a_setup.exe", "b_setup.msi", "Deploy.ps1",
                    Path.Combine("Files", "inner.msi"),
                    Path.Combine("Files", "deep", "zeta.exe"),
                ],
                all.RelativePaths);
            Assert.Equal(0, all.Omitted);

            var capped = PackagingFlow.FindInstallers(folder, max: 2);
            Assert.Equal(["a_setup.exe", "b_setup.msi"], capped.RelativePaths);
            Assert.Equal(3, capped.Omitted);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.Empty(PackagingFlow.FindInstallers(null).RelativePaths);
        Assert.Empty(PackagingFlow.FindInstallers(Path.Combine(Path.GetTempPath(), "wraptune-nope")).RelativePaths);
    }

    [Theory]
    [InlineData(DropKind.SourceFolder, "drop to use as the source folder")]
    [InlineData(DropKind.OutputFolder, "drop to use as the output folder")]
    [InlineData(DropKind.SetupFile, "drop to use as the setup file")]
    [InlineData(DropKind.InspectPackage, "drop to inspect this package")]
    public void Drop_hint_says_what_letting_go_will_do(DropKind kind, string expected)
        => Assert.Equal(expected, PackagingFlow.DropHint(kind));

    [Fact]
    public void Receipt_summary_lists_size_time_and_signing()
    {
        Assert.Equal("39.9 MB · built in 00:04",
            PackagingFlow.ReceiptSummary(39_900_000, TimeSpan.FromSeconds(4), null));
        Assert.Equal("1.5 KB · built in 01:05 · signed · azure",
            PackagingFlow.ReceiptSummary(1_500, TimeSpan.FromSeconds(65), "signed · azure"));
    }

    [Theory]
    [InlineData(0, "0 of 3 paths set")]
    [InlineData(2, "2 of 3 paths set")]
    [InlineData(3, "ready — wrap when you are")]
    public void Footer_status_reflects_path_progress(int set, string expected)
        => Assert.Equal(expected, PackagingFlow.PathsStatus(set, signingOn: false));

    [Fact]
    public void Footer_status_mentions_signing_when_enabled()
        => Assert.Equal("ready — sign, then wrap", PackagingFlow.PathsStatus(3, signingOn: true));

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(4, "00:04")]
    [InlineData(65, "01:05")]
    [InlineData(3599, "59:59")]
    public void Elapsed_formats_as_minutes_seconds(int seconds, string expected)
        => Assert.Equal(expected, PackagingFlow.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(38_200_000, "38.2 MB")]
    [InlineData(1_500, "1.5 KB")]
    [InlineData(2_147_483_648, "2.1 GB")]
    public void Sizes_format_with_one_decimal(long bytes, string expected)
        => Assert.Equal(expected, PackagingFlow.FormatSize(bytes));

    [Fact]
    public void Source_summary_combines_count_and_size()
        => Assert.Equal("6 files · 38.2 MB", PackagingFlow.SourceSummary(6, 38_200_000));

    [Fact]
    public void Default_output_folder_is_the_source_folders_parent()
    {
        Assert.Equal("/Users/x/Scratch", PackagingFlow.DefaultOutputFolder("/Users/x/Scratch/wraptune"));
        Assert.Equal("/Users/x/Scratch", PackagingFlow.DefaultOutputFolder("/Users/x/Scratch/wraptune/"));
        Assert.Null(PackagingFlow.DefaultOutputFolder("/"));
        Assert.Null(PackagingFlow.DefaultOutputFolder("  "));
    }

    [Fact]
    public void Msi_readout_shows_code_version_and_context()
    {
        var msi = new MsiInfo
        {
            MsiProductCode = "{033EAB32-7BFC-4921-AE12-E9D1ACC747ED}",
            MsiProductVersion = "1.0.0.0",
            MsiExecutionContext = 0,
        };
        Assert.Equal("msi {033EAB32-7BFC-4921-AE12-E9D1ACC747ED} · 1.0.0.0 · per-machine",
            PackagingFlow.MsiReadout(msi));

        Assert.Equal("msi {X} · 2.0 · dual-purpose",
            PackagingFlow.MsiReadout(new MsiInfo { MsiProductCode = "{X}", MsiProductVersion = "2.0", MsiExecutionContext = 2 }));
    }

    [Theory]
    [InlineData("/x/folder", true, false, DropKind.SourceFolder)]
    [InlineData("/x/folder", true, true, DropKind.OutputFolder)]
    [InlineData("/x/setup.msi", false, false, DropKind.SetupFile)]
    [InlineData("/x/app.intunewin", false, false, DropKind.InspectPackage)]
    [InlineData("/x/APP.INTUNEWIN", false, true, DropKind.InspectPackage)]
    public void Window_drops_route_by_what_was_dropped(string path, bool isDir, bool overOutput, DropKind expected)
        => Assert.Equal(expected, PackagingFlow.ClassifyDrop(path, isDir, overOutput));

    [Fact]
    public void Sign_stage_detail_reflects_the_mode_not_a_stale_pfx_path()
    {
        Assert.Equal("contoso-ov.pfx · timestamped",
            PackagingFlow.SignStageDetail(CertMode.Pfx, "/certs/contoso-ov.pfx"));
        Assert.Equal("hsm · timestamped",
            PackagingFlow.SignStageDetail(CertMode.Pkcs11, "/certs/contoso-ov.pfx"));
        Assert.Equal("azure · timestamped",
            PackagingFlow.SignStageDetail(CertMode.TrustedSigning, "/certs/leftover.pfx"));
    }

    [Fact]
    public void Overall_fraction_walks_stages_and_moves_within_encrypt()
    {
        // 5 visible stages (no signing), encrypt (index 1 of Zip,Encrypt,Msi,Detection,Assemble
        // stage list) at 50% → 1 full stage + half a stage out of 5.
        var f = PackagingFlow.OverallFraction(totalStages: 5, completedStages: 1, inStagePercent: 50);
        Assert.Equal(0.3, f, precision: 5);

        Assert.Equal(0.0, PackagingFlow.OverallFraction(5, 0, null), precision: 5);
        Assert.Equal(1.0, PackagingFlow.OverallFraction(5, 5, null), precision: 5);
    }
}
