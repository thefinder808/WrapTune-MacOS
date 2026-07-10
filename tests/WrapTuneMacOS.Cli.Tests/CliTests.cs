using WrapTuneMacOS.Cli;
using WrapTuneMacOS.Packaging;
using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Cli.Tests;

/// <summary>
/// Drives Cli.RunAsync — the exact code path the wraptune binary executes —
/// with in-memory writers, so the whole command surface is tested offline.
/// </summary>
public sealed class CliTests : IDisposable
{
    private readonly string _root;
    private readonly string _src;
    private readonly string _out;

    public CliTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wraptune-cli-test-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_root, "source");
        _out = Path.Combine(_root, "out");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_src, "setup.ps1"), "Write-Host hi");
        File.WriteAllText(Path.Combine(_src, "readme.txt"), "docs");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Cli.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string PackagePath => Path.Combine(_out, "setup.intunewin");

    // ── pack ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pack_creates_a_valid_package()
    {
        var r = await RunAsync("pack", "-c", _src, "-s", Path.Combine(_src, "setup.ps1"), "-o", _out);

        Assert.Equal(0, r.Exit);
        Assert.Contains(PackagePath, r.Out);
        Assert.True(File.Exists(PackagePath));
        Assert.True(IntuneWinReader.Read(PackagePath).IsValid);
    }

    [Fact]
    public async Task Official_tool_style_flags_without_a_subcommand_mean_pack()
    {
        var r = await RunAsync("-c", _src, "-s", Path.Combine(_src, "setup.ps1"), "-o", _out);

        Assert.Equal(0, r.Exit);
        Assert.True(File.Exists(PackagePath));
    }

    [Fact]
    public async Task Pack_without_required_options_prints_usage_and_exits_2()
    {
        var r = await RunAsync("pack", "-c", _src);

        Assert.Equal(2, r.Exit);
        Assert.Contains("--setup", r.Err);
    }

    [Fact]
    public async Task Existing_output_needs_overwrite_and_quiet_implies_it()
    {
        Assert.Equal(0, (await RunAsync("pack", "-c", _src, "-s", Path.Combine(_src, "setup.ps1"), "-o", _out)).Exit);

        var second = await RunAsync("pack", "-c", _src, "-s", Path.Combine(_src, "setup.ps1"), "-o", _out);
        Assert.Equal(1, second.Exit);   // exists, no --overwrite

        // -q mirrors the official tool: quiet mode silently overwrites.
        var quiet = await RunAsync("pack", "-q", "-c", _src, "-s", Path.Combine(_src, "setup.ps1"), "-o", _out);
        Assert.Equal(0, quiet.Exit);
    }

    [Fact]
    public async Task Unknown_flag_is_a_usage_error()
    {
        var r = await RunAsync("pack", "--sourcefolder", _src);
        Assert.Equal(2, r.Exit);
    }

    // ── inspect ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inspect_reports_a_valid_package_and_exits_0()
    {
        await RunAsync("pack", "-c", _src, "-s", Path.Combine(_src, "setup.ps1"), "-o", _out);

        var r = await RunAsync("inspect", PackagePath);

        Assert.Equal(0, r.Exit);
        Assert.Contains("setup.ps1", r.Out);
        Assert.Contains("VALID", r.Out);
        Assert.Contains("readme.txt", r.Out);
    }

    [Fact]
    public async Task Inspect_flags_a_tampered_package_and_exits_1()
    {
        await RunAsync("pack", "-c", _src, "-s", Path.Combine(_src, "setup.ps1"), "-o", _out);
        var tampered = Path.Combine(_out, "tampered.intunewin");
        TamperLastContentByte(PackagePath, tampered);

        var r = await RunAsync("inspect", tampered);

        Assert.Equal(1, r.Exit);
        Assert.Contains("FAIL", r.Out);
    }

    // ── extract ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Extract_writes_the_decrypted_payload_zip()
    {
        await RunAsync("pack", "-c", _src, "-s", Path.Combine(_src, "setup.ps1"), "-o", _out);

        var r = await RunAsync("extract", PackagePath, "-o", _root);

        Assert.Equal(0, r.Exit);
        var dest = Path.Combine(_root, "setup-payload.zip");
        Assert.True(File.Exists(dest));
        using var zip = System.IO.Compression.ZipFile.OpenRead(dest);
        Assert.NotNull(zip.GetEntry("setup.ps1"));
    }

    // ── shared plumbing ─────────────────────────────────────────────────────

    [Fact]
    public async Task Help_prints_usage_and_exits_0()
    {
        var r = await RunAsync("--help");
        Assert.Equal(0, r.Exit);
        Assert.Contains("pack", r.Out);
        Assert.Contains("inspect", r.Out);
        Assert.Contains("extract", r.Out);
    }

    [Fact]
    public async Task Unknown_command_exits_2()
    {
        var r = await RunAsync("frobnicate");
        Assert.Equal(2, r.Exit);
    }

    [Fact]
    public void Signing_options_map_modes_and_take_secrets_from_env_only()
    {
        // PFX: secret comes from WRAPTUNE_SIGN_SECRET.
        var pfx = Cli.BuildSigningOptions(
            new Dictionary<string, string> { ["--pfx"] = "/tmp/cert.pfx" },
            name => name == "WRAPTUNE_SIGN_SECRET" ? "hunter2" : null);
        Assert.NotNull(pfx);
        Assert.Equal(CertMode.Pfx, pfx!.CertMode);
        Assert.Equal("/tmp/cert.pfx", pfx.PfxPath);
        Assert.Equal("hunter2", pfx.Secret);

        // Azure: the pasted token env maps to Secret (WrapTune's TS convention —
        // PayloadSigner routes it to the engine's access token, never its Secret).
        var azure = Cli.BuildSigningOptions(
            new Dictionary<string, string>
            {
                ["--azure-endpoint"] = "eus.codesigning.azure.net",
                ["--azure-account"] = "acct",
                ["--azure-profile"] = "prof",
            },
            name => name == "WRAPTUNE_AZURE_TOKEN" ? "tok" : null);
        Assert.NotNull(azure);
        Assert.Equal(CertMode.TrustedSigning, azure!.CertMode);
        Assert.Equal("tok", azure.Secret);

        // No signing flags → no signing.
        Assert.Null(Cli.BuildSigningOptions(new Dictionary<string, string>(), _ => null));

        // Two modes at once is a usage error.
        Assert.Throws<CliUsageException>(() => Cli.BuildSigningOptions(
            new Dictionary<string, string> { ["--pfx"] = "a", ["--pkcs11-module"] = "b" },
            _ => null));
    }

    private static void TamperLastContentByte(string src, string dest)
    {
        const string contentsEntry = "IntuneWinPackage/Contents/IntunePackage.intunewin";
        const string metaEntry = "IntuneWinPackage/Metadata/Detection.xml";
        byte[] blob, meta;
        using (var zin = System.IO.Compression.ZipFile.OpenRead(src))
        {
            blob = ReadAll(zin.GetEntry(contentsEntry)!);
            meta = ReadAll(zin.GetEntry(metaEntry)!);
        }
        blob[^1] ^= 0xFF;
        using var zout = System.IO.Compression.ZipFile.Open(dest, System.IO.Compression.ZipArchiveMode.Create);
        using (var s = zout.CreateEntry(contentsEntry).Open()) s.Write(blob);
        using (var s = zout.CreateEntry(metaEntry).Open()) s.Write(meta);
    }

    private static byte[] ReadAll(System.IO.Compression.ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
