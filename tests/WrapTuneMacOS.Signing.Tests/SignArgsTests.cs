using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

/// <summary>
/// Offline tests of the osslsigncode argument builder — no binary required, so
/// they always run. Covers PFX vs PKCS#11 mode, optional flags, and the secure
/// <c>-readpass</c> path (the secret is never an argument value itself).
/// </summary>
public sealed class SignArgsTests
{
    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void Pfx_mode_builds_expected_pkcs12_invocation()
    {
        var o = new SigningOptions
        {
            CertMode = CertMode.Pfx,
            PfxPath = "/certs/code.pfx",
            TimestampUrl = "http://timestamp.digicert.com",
            Description = "My App",
            Url = "https://example.com",
        };

        var args = PayloadSigner.BuildSignArgs("/src/app.exe", "/src/app.exe.signtmp", o, "/tmp/pin");

        Assert.Equal("sign", args[0]);
        Assert.Equal("/certs/code.pfx", ValueAfter(args, "-pkcs12"));
        Assert.Equal("sha256", ValueAfter(args, "-h"));
        Assert.Equal("My App", ValueAfter(args, "-n"));
        Assert.Equal("https://example.com", ValueAfter(args, "-i"));
        Assert.Equal("http://timestamp.digicert.com", ValueAfter(args, "-ts"));   // RFC3161
        Assert.Equal("/tmp/pin", ValueAfter(args, "-readpass"));
        Assert.Equal("/src/app.exe", ValueAfter(args, "-in"));
        Assert.Equal("/src/app.exe.signtmp", ValueAfter(args, "-out"));
        Assert.DoesNotContain("-pkcs11module", args);
    }

    [Fact]
    public void Pkcs11_mode_builds_token_invocation_and_omits_pkcs12()
    {
        var o = new SigningOptions
        {
            CertMode = CertMode.Pkcs11,
            Pkcs11ModulePath = "/usr/local/lib/pkcs11.dylib",
            Pkcs11CertUri = "pkcs11:token=tok;object=cert",
            KeyUri = "pkcs11:token=tok;object=key",
        };

        var args = PayloadSigner.BuildSignArgs("/src/app.msi", "/src/app.msi.signtmp", o, "/tmp/pin");

        Assert.Equal("/usr/local/lib/pkcs11.dylib", ValueAfter(args, "-pkcs11module"));
        Assert.Equal("pkcs11:token=tok;object=cert", ValueAfter(args, "-pkcs11cert"));
        Assert.Equal("pkcs11:token=tok;object=key", ValueAfter(args, "-key"));
        Assert.DoesNotContain("-pkcs12", args);
    }

    [Fact]
    public void No_secret_omits_readpass()
    {
        var o = new SigningOptions { CertMode = CertMode.Pfx, PfxPath = "/c.pfx" };
        var args = PayloadSigner.BuildSignArgs("/in", "/out", o, passFile: null);
        Assert.DoesNotContain("-readpass", args);
    }

    [Fact]
    public void No_timestamp_url_omits_ts()
    {
        var o = new SigningOptions { CertMode = CertMode.Pfx, PfxPath = "/c.pfx", TimestampUrl = "" };
        var args = PayloadSigner.BuildSignArgs("/in", "/out", o, null);
        Assert.DoesNotContain("-ts", args);
    }

    [Fact]
    public void Secret_value_never_appears_as_an_argument()
    {
        // The PIN/password is supplied only via the -readpass FILE, never inline.
        var o = new SigningOptions { CertMode = CertMode.Pfx, PfxPath = "/c.pfx", Secret = "hunter2" };
        var args = PayloadSigner.BuildSignArgs("/in", "/out", o, "/tmp/pin");
        Assert.DoesNotContain("hunter2", args);
        Assert.DoesNotContain("-pass", args);   // never the insecure command-line form
    }
}
