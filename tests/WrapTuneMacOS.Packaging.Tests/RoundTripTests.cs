using System.IO.Compression;
using System.Security.Cryptography;

namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// Round-trip self-tests: package a source folder, then decrypt our own output
/// and prove HMAC, FileDigest, size, and the payload all check out. This is the
/// cheapest rung of the verification ladder and catches every layout/HMAC/
/// padding mistake locally.
/// </summary>
public sealed class RoundTripTests
{
    private static async Task<(PackageResult result, TestWorkspace ws)> PackSampleAsync()
    {
        var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", "fake installer payload"u8.ToArray());
        ws.AddSourceFile("data/readme.txt", "hello from a nested file");
        ws.AddSourceFile("config.json", """{ "k": "v" }""");

        var writer = new IntuneWinWriter();
        var result = await writer.PackageAsync(new PackageRequest(
            SourceFolder: ws.Source,
            SetupFile: Path.Combine(ws.Source, "setup.exe"),
            OutputFolder: ws.Output,
            Overwrite: true));
        return (result, ws);
    }

    [Fact]
    public async Task Packages_and_round_trips_successfully()
    {
        var (result, ws) = await PackSampleAsync();
        using var _ = ws;

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.OutputPath);
        Assert.Equal(Path.Combine(ws.Output, "setup.intunewin"), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));

        var contents = IntuneWinReader.Read(result.OutputPath!);
        Assert.True(contents.MacValid, "HMAC over IV+ciphertext must match the recorded MAC.");
        Assert.True(contents.DigestValid, "SHA-256 of the decrypted payload must match FileDigest.");
        Assert.True(contents.SizeValid, "Decrypted size must match UnencryptedContentSize.");
        Assert.True(contents.IsValid);
    }

    [Fact]
    public async Task Recovered_payload_matches_the_source_files()
    {
        var (result, ws) = await PackSampleAsync();
        using var _ = ws;

        var contents = IntuneWinReader.Read(result.OutputPath!);
        var recovered = TestWorkspace.ReadZipEntries(contents.DecryptedZip);

        Assert.Equal("fake installer payload"u8.ToArray(), recovered["setup.exe"]);
        Assert.Equal("hello from a nested file", System.Text.Encoding.UTF8.GetString(recovered["data/readme.txt"]));
        Assert.Equal("""{ "k": "v" }""", System.Text.Encoding.UTF8.GetString(recovered["config.json"]));
        Assert.Equal(3, recovered.Count);
    }

    [Fact]
    public async Task Detection_xml_records_expected_metadata()
    {
        var (result, ws) = await PackSampleAsync();
        using var _ = ws;

        var d = IntuneWinReader.Read(result.OutputPath!).Detection;
        Assert.Equal("setup.exe", d.Name);
        Assert.Equal("setup.exe", d.SetupFile);                 // root-level → just the name
        Assert.Equal(IntuneWinWriter.ContentFileName, d.FileName);
        Assert.Equal("ProfileVersion1", d.EncryptionInfo.ProfileIdentifier);
        Assert.Equal("SHA256", d.EncryptionInfo.FileDigestAlgorithm);
        Assert.Null(d.MsiInfo);                                  // EXE input → no MSI metadata
    }

    [Fact]
    public async Task Nested_setup_file_is_recorded_with_backslashes()
    {
        var ws = new TestWorkspace();
        using var _ = ws;
        ws.AddSourceFile("installers/app/setup.exe", "x"u8.ToArray());

        var result = await new IntuneWinWriter().PackageAsync(new PackageRequest(
            ws.Source, Path.Combine(ws.Source, "installers/app/setup.exe"), ws.Output, Overwrite: true));

        Assert.True(result.Success, result.Error);
        var d = IntuneWinReader.Read(result.OutputPath!).Detection;
        Assert.Equal(@"installers\app\setup.exe", d.SetupFile);  // Windows-style for the Windows endpoint
    }

    [Fact]
    public async Task Keys_are_distinct_and_correctly_sized()
    {
        var (result, ws) = await PackSampleAsync();
        using var _ = ws;

        var enc = IntuneWinReader.Read(result.OutputPath!).Detection.EncryptionInfo;
        var key = Convert.FromBase64String(enc.EncryptionKey);
        var macKey = Convert.FromBase64String(enc.MacKey);
        var iv = Convert.FromBase64String(enc.InitializationVector);

        Assert.Equal(32, key.Length);
        Assert.Equal(32, macKey.Length);
        Assert.Equal(16, iv.Length);
        Assert.False(key.SequenceEqual(macKey), "EncryptionKey and MacKey must be independent.");
    }

    [Fact]
    public async Task Each_run_uses_fresh_random_keys()
    {
        var (r1, ws1) = await PackSampleAsync(); using var _1 = ws1;
        var (r2, ws2) = await PackSampleAsync(); using var _2 = ws2;

        var e1 = IntuneWinReader.Read(r1.OutputPath!).Detection.EncryptionInfo;
        var e2 = IntuneWinReader.Read(r2.OutputPath!).Detection.EncryptionInfo;

        Assert.NotEqual(e1.EncryptionKey, e2.EncryptionKey);
        Assert.NotEqual(e1.InitializationVector, e2.InitializationVector);
        // FileDigest is over the (deterministic-ish) payload, so it may match —
        // identity of the encrypted artifact comes from the random keys.
    }

    [Fact]
    public async Task Refuses_to_overwrite_unless_allowed()
    {
        var ws = new TestWorkspace();
        using var _ = ws;
        ws.AddSourceFile("setup.exe", "x"u8.ToArray());
        var req = new PackageRequest(ws.Source, Path.Combine(ws.Source, "setup.exe"), ws.Output, Overwrite: false);
        var writer = new IntuneWinWriter();

        var first = await writer.PackageAsync(req);
        Assert.True(first.Success, first.Error);

        var second = await writer.PackageAsync(req);     // exists, overwrite disabled
        Assert.False(second.Success);
        Assert.Contains("overwrite", second.Error!, StringComparison.OrdinalIgnoreCase);

        var third = await writer.PackageAsync(req with { Overwrite = true });
        Assert.True(third.Success, third.Error);
    }

    [Fact]
    public async Task Accepts_source_folder_with_trailing_separator()
    {
        var ws = new TestWorkspace();
        using var _ = ws;
        ws.AddSourceFile("setup.ps1", "x"u8.ToArray());

        // A trailing slash on the source folder (as folder pickers often return)
        // must not break the "setup file is inside" check.
        var result = await new IntuneWinWriter().PackageAsync(new PackageRequest(
            ws.Source + Path.DirectorySeparatorChar,
            Path.Combine(ws.Source, "setup.ps1"),
            ws.Output, Overwrite: true));

        Assert.True(result.Success, result.Error);
        Assert.Equal("setup.ps1", IntuneWinReader.Read(result.OutputPath!).Detection.SetupFile);
    }

    [Fact]
    public async Task Rejects_setup_file_outside_source_folder()
    {
        var ws = new TestWorkspace();
        using var _ = ws;
        ws.AddSourceFile("setup.exe", "x"u8.ToArray());
        var outside = Path.Combine(ws.Root, "elsewhere.exe");
        File.WriteAllBytes(outside, "y"u8.ToArray());

        var result = await new IntuneWinWriter().PackageAsync(
            new PackageRequest(ws.Source, outside, ws.Output, Overwrite: true));

        Assert.False(result.Success);
        Assert.Contains("inside the source folder", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tampered_payload_fails_mac_validation()
    {
        var (result, ws) = await PackSampleAsync();
        using var _ = ws;

        // Flip a ciphertext byte inside the package and confirm the MAC catches it.
        var tampered = Path.Combine(ws.Output, "tampered.intunewin");
        RewriteWithFlippedContentByte(result.OutputPath!, tampered);

        var contents = IntuneWinReader.Read(tampered);
        Assert.False(contents.MacValid, "A modified payload must fail HMAC validation.");
    }

    private static void RewriteWithFlippedContentByte(string srcPackage, string destPackage)
    {
        const string contentsEntry = "IntuneWinPackage/Contents/" + IntuneWinWriter.ContentFileName;
        const string metaEntry = "IntuneWinPackage/Metadata/Detection.xml";

        byte[] blob, meta;
        using (var zin = ZipFile.OpenRead(srcPackage))
        {
            blob = ReadAll(zin.GetEntry(contentsEntry)!);
            meta = ReadAll(zin.GetEntry(metaEntry)!);
        }
        blob[^1] ^= 0xFF;   // flip the last ciphertext byte

        using var zout = ZipFile.Open(destPackage, ZipArchiveMode.Create);
        using (var s = zout.CreateEntry(contentsEntry).Open()) s.Write(blob);
        using (var s = zout.CreateEntry(metaEntry).Open()) s.Write(meta);
    }

    private static byte[] ReadAll(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
