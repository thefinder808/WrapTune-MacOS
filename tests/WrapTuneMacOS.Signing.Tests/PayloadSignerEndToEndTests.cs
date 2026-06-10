using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

/// <summary>
/// End-to-end: sign in-process with the MacSign engine (throwaway self-signed cert
/// from <c>openssl</c>), then have the real <c>osslsigncode</c> binary — code that
/// shares nothing with the engine — confirm the signature. Self-skips (early
/// return — xUnit 2.x has no dynamic skip) when either tool is absent; CI installs
/// both via <c>brew install osslsigncode</c>.
/// </summary>
public sealed class PayloadSignerEndToEndTests
{
    [Fact]
    public async Task Signs_a_script_in_place_and_osslsigncode_confirms_the_signature()
    {
        var ossl = SigningTestEnv.LocateOsslsigncode();
        if (ossl is null) return;                          // osslsigncode not installed — skip.
        if (!await SigningTestEnv.HasOpenSslAsync()) return; // openssl not available — skip.

        var dir = Path.Combine(Path.GetTempPath(), "wt-sign-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pfx = await SigningTestEnv.CreateSelfSignedPfxAsync(dir);

            var script = Path.Combine(dir, "install.ps1");
            await File.WriteAllTextAsync(script, "Write-Host 'hello from WrapTune'\n");

            var options = new SigningOptions
            {
                CertMode = CertMode.Pfx,
                PfxPath = pfx,
                Secret = SigningTestEnv.Password,
                TimestampUrl = null,   // no network dependency in the test
            };

            var signer = PayloadSigner.TryCreate(options, out var err);
            Assert.NotNull(signer);
            Assert.Null(err);

            var result = await signer!.SignAsync(dir, script, options);
            Assert.True(result.Success, result.Error);

            // The independent verifier must now find a signature on the file.
            var (_, vOut, vErr) = await ProcessRunner.RunAsync(ossl, ["verify", "-in", script]);
            Assert.DoesNotContain("No signature found", vOut + "\n" + vErr, StringComparison.OrdinalIgnoreCase);

            // Re-running must SKIP the now-signed file (don't clobber an existing
            // signature) — the engine detects it in-process, no external verify.
            var rerun = await signer.SignAsync(dir, script, options);
            Assert.True(rerun.Success, rerun.Error);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Signs_every_format_ps1_pe_and_msi_in_one_run()
    {
        var ossl = SigningTestEnv.LocateOsslsigncode();
        if (ossl is null) return;                          // osslsigncode not installed — skip.
        if (!await SigningTestEnv.HasOpenSslAsync()) return; // openssl not available — skip.

        var dir = Path.Combine(Path.GetTempPath(), "wt-sign-all-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pfx = await SigningTestEnv.CreateSelfSignedPfxAsync(dir);

            // One payload of each signable format. The signing lib's own assembly
            // stands in as a real PE; the committed fixture is a real (tiny) MSI.
            var source = Path.Combine(dir, "source");
            Directory.CreateDirectory(source);
            var script = Path.Combine(source, "install.ps1");
            await File.WriteAllTextAsync(script, "Write-Host 'install'\n");
            var pe = Path.Combine(source, "helper.dll");
            File.Copy(typeof(PayloadSigner).Assembly.Location, pe);
            var msi = Path.Combine(source, "app.msi");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "test.msi"), msi);

            var options = new SigningOptions
            {
                CertMode = CertMode.Pfx,
                PfxPath = pfx,
                Secret = SigningTestEnv.Password,
                SignAllSignableFiles = true,
            };

            var signer = PayloadSigner.TryCreate(options, out var err);
            Assert.NotNull(signer);

            var result = await signer!.SignAsync(source, script, options);
            Assert.True(result.Success, result.Error);

            // The independent verifier must find a signature on every format.
            foreach (var file in new[] { script, pe, msi })
            {
                var (_, vOut, vErr) = await ProcessRunner.RunAsync(ossl, ["verify", "-in", file]);
                Assert.DoesNotContain("No signature found", vOut + "\n" + vErr, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
