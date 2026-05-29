using System.IO.Compression;

namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// Asserts the on-disk shape of the package: the two OPC entries at their exact
/// paths, and the encrypted blob's <c>Mac(32) || IV(16) || ciphertext</c> layout.
/// </summary>
public sealed class FormatStructureTests
{
    private const string ContentsEntry = "IntuneWinPackage/Contents/" + IntuneWinWriter.ContentFileName;
    private const string MetaEntry = "IntuneWinPackage/Metadata/Detection.xml";

    private static async Task<(string package, TestWorkspace ws)> PackAsync()
    {
        var ws = new TestWorkspace();
        ws.AddSourceFile("setup.exe", "payload"u8.ToArray());
        var result = await new IntuneWinWriter().PackageAsync(
            new PackageRequest(ws.Source, Path.Combine(ws.Source, "setup.exe"), ws.Output, Overwrite: true));
        Assert.True(result.Success, result.Error);
        return (result.OutputPath!, ws);
    }

    [Fact]
    public async Task Package_has_exactly_the_two_opc_entries()
    {
        var (package, ws) = await PackAsync();
        using var _ = ws;

        using var zip = ZipFile.OpenRead(package);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { ContentsEntry, MetaEntry }, names);
    }

    [Fact]
    public async Task Encrypted_blob_layout_is_mac_iv_ciphertext()
    {
        var (package, ws) = await PackAsync();
        using var _ = ws;

        byte[] blob;
        using (var zip = ZipFile.OpenRead(package))
        using (var s = zip.GetEntry(ContentsEntry)!.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            blob = ms.ToArray();
        }

        // Mac(32) + IV(16) + at least one AES block (16) of ciphertext.
        Assert.True(blob.Length >= 32 + 16 + 16);
        Assert.Equal(0, (blob.Length - 32 - 16) % 16);   // ciphertext is whole AES blocks

        var detection = IntuneWinReader.Read(package).Detection;
        var recordedMac = Convert.FromBase64String(detection.EncryptionInfo.Mac);
        var recordedIv = Convert.FromBase64String(detection.EncryptionInfo.InitializationVector);

        Assert.Equal(recordedMac, blob.AsSpan(0, 32).ToArray());    // leading 32 bytes == MAC
        Assert.Equal(recordedIv, blob.AsSpan(32, 16).ToArray());    // next 16 bytes == IV
    }
}
