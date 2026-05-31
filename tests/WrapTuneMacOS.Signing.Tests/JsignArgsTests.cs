using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

/// <summary>
/// Offline tests of the jsign (Azure Trusted Signing) argument builder — no binary
/// required. The decisive security check: the access token is referenced only as
/// <c>env:WT_TS_TOKEN</c> and never appears as an argument value.
/// </summary>
public sealed class JsignArgsTests
{
    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static SigningOptions TrustedSigningOptions() => new()
    {
        CertMode = CertMode.TrustedSigning,
        TrustedSigningEndpoint = "weu.codesigning.azure.net",
        TrustedSigningAccount = "myaccount",
        TrustedSigningProfile = "myprofile",
        Secret = "super-secret-token",   // must never reach argv
    };

    [Fact]
    public void Builds_expected_trusted_signing_invocation()
    {
        var args = PayloadSigner.BuildJsignArgs("/src/app.exe", TrustedSigningOptions());

        Assert.Equal("TRUSTEDSIGNING", ValueAfter(args, "--storetype"));
        Assert.Equal("weu.codesigning.azure.net", ValueAfter(args, "--keystore"));
        Assert.Equal("myaccount/myprofile", ValueAfter(args, "--alias"));
        Assert.Equal("env:WT_TS_TOKEN", ValueAfter(args, "--storepass"));
        Assert.Equal("/src/app.exe", args[^1]);   // file is signed in place, last arg
    }

    [Fact]
    public void Token_value_is_never_an_argument()
    {
        var args = PayloadSigner.BuildJsignArgs("/src/app.exe", TrustedSigningOptions());
        Assert.DoesNotContain("super-secret-token", args);
    }

    [Fact]
    public void No_explicit_timestamp_flag_jsign_auto_timestamps()
    {
        var args = PayloadSigner.BuildJsignArgs("/src/app.exe", TrustedSigningOptions());
        Assert.DoesNotContain("--tsaurl", args);
        Assert.DoesNotContain("-ts", args);
    }

    [Fact]
    public void Name_and_url_are_passed_when_set_and_omitted_when_blank()
    {
        var withMeta = TrustedSigningOptions() with { Description = "My App", Url = "https://example.com" };
        var a1 = PayloadSigner.BuildJsignArgs("/x.msi", withMeta);
        Assert.Equal("My App", ValueAfter(a1, "--name"));
        Assert.Equal("https://example.com", ValueAfter(a1, "--url"));

        var a2 = PayloadSigner.BuildJsignArgs("/x.msi", TrustedSigningOptions());
        Assert.DoesNotContain("--name", a2);
        Assert.DoesNotContain("--url", a2);
    }
}
