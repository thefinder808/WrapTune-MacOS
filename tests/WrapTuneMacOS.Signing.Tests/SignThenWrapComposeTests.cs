using WrapTuneMacOS.Packaging;
using WrapTuneMacOS.Signing;
using System.IO.Compression;

namespace WrapTuneMacOS.Signing.Tests;

/// <summary>
/// Proves the whole feature composes: sign the payload in place, then wrap it
/// with the unchanged <c>.intunewin</c> engine. Because signing happens before
/// the engine zips and hashes, the package's FileDigest/HMAC/size must validate
/// over the SIGNED bytes, and the recovered payload must still carry the
/// signature. Self-skips when osslsigncode / openssl are absent.
/// </summary>
public sealed class SignThenWrapComposeTests
{
    [Fact]
    public async Task Signed_payload_wraps_into_a_valid_package_and_stays_signed()
    {
        var ossl = SignerLocator.Locate();
        if (ossl is null) return;
        if (!await SigningTestEnv.HasOpenSslAsync()) return;

        var root = Path.Combine(Path.GetTempPath(), "wt-compose-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        try
        {
            var pfx = await SigningTestEnv.CreateSelfSignedPfxAsync(root);
            var script = Path.Combine(source, "install.ps1");
            await File.WriteAllTextAsync(script, "Write-Host 'install'\n");

            // 1. Sign the payload in place.
            var options = new SigningOptions { CertMode = CertMode.Pfx, PfxPath = pfx, Secret = SigningTestEnv.Password };
            var signer = PayloadSigner.TryCreate(options, out _);
            Assert.NotNull(signer);
            Assert.True((await signer!.SignAsync(source, script, options)).Success);

            // 2. Wrap with the real (unchanged) engine.
            var pkg = await new IntuneWinWriter()
                .PackageAsync(new PackageRequest(source, script, output, Overwrite: true));
            Assert.True(pkg.Success, pkg.Error);

            // 3. The package must self-validate — digest/HMAC/size computed over the SIGNED payload.
            var contents = IntuneWinReader.Read(pkg.OutputPath!);
            Assert.True(contents.IsValid, "FileDigest/HMAC/size must validate over the signed payload.");

            // 4. The recovered payload must still be signed.
            var recovered = ExtractEntry(contents.DecryptedZip, "install.ps1");
            var verifyPath = Path.Combine(root, "recovered.ps1");
            await File.WriteAllBytesAsync(verifyPath, recovered);
            var (_, vOut, vErr) = await ProcessRunner.RunAsync(ossl, ["verify", "-in", verifyPath]);
            Assert.DoesNotContain("No signature found", vOut + "\n" + vErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static byte[] ExtractEntry(byte[] zipBytes, string entryName)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.Entries.First(e => e.FullName == entryName);
        using var s = entry.Open();
        using var buf = new MemoryStream();
        s.CopyTo(buf);
        return buf.ToArray();
    }
}
