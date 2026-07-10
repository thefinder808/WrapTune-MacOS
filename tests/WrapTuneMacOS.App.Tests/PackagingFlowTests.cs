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
