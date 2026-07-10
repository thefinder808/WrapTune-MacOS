namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// Tests for the shared inspection core — the one code path behind both the
/// CLI's `inspect` command and the GUI's Inspect Package window.
/// </summary>
public sealed class PackageInspectorTests
{
    private static async Task<(string package, TestWorkspace ws)> PackSampleAsync()
    {
        var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", "fake installer payload"u8.ToArray());
        ws.AddSourceFile("data/readme.txt", "hello");

        var result = await new IntuneWinWriter().PackageAsync(new PackageRequest(
            ws.Source, Path.Combine(ws.Source, "setup.exe"), ws.Output, Overwrite: true));
        Assert.True(result.Success, result.Error);
        return (result.OutputPath!, ws);
    }

    [Fact]
    public async Task Inspect_reports_metadata_validity_and_payload_contents()
    {
        var (package, ws) = await PackSampleAsync();
        using var _ = ws;

        var i = PackageInspector.Inspect(package);

        Assert.True(i.MacValid);
        Assert.True(i.DigestValid);
        Assert.True(i.SizeValid);
        Assert.True(i.IsValid);
        Assert.Equal("setup.exe", i.Detection.Name);
        Assert.Equal("setup.exe", i.Detection.SetupFile);
        Assert.Equal(2, i.PayloadEntryCount);
        Assert.Contains("setup.exe", i.PayloadEntries);
        Assert.Contains("data/readme.txt", i.PayloadEntries);
        Assert.True(i.EncryptedSizeBytes > 48);   // Mac(32) + IV(16) + ciphertext
    }

    [Fact]
    public async Task Inspect_flags_a_tampered_package_and_lists_no_payload()
    {
        var (package, ws) = await PackSampleAsync();
        using var _ = ws;

        var tampered = Path.Combine(ws.Output, "tampered.intunewin");
        TamperLastContentByte(package, tampered);

        var i = PackageInspector.Inspect(tampered);

        Assert.False(i.MacValid);
        Assert.False(i.IsValid);
        Assert.Equal(0, i.PayloadEntryCount);
        Assert.Empty(i.PayloadEntries);
    }

    [Fact]
    public async Task ExtractPayloadZip_writes_the_decrypted_payload()
    {
        var (package, ws) = await PackSampleAsync();
        using var _ = ws;

        var dest = Path.Combine(ws.Output, "payload.zip");
        var r = PackageInspector.ExtractPayloadZip(package, dest);

        Assert.True(r.IsValid);
        var entries = TestWorkspace.ReadZipEntries(File.ReadAllBytes(dest));
        Assert.Equal("fake installer payload"u8.ToArray(), entries["setup.exe"]);
    }

    private static void TamperLastContentByte(string src, string dest)
    {
        const string contentsEntry = "IntuneWinPackage/Contents/" + IntuneWinWriter.ContentFileName;
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
