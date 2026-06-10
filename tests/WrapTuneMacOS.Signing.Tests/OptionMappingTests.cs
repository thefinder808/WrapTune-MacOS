using WrapTuneMacOS.Signing;
using EngineCertMode = MacSign.Signing.CertMode;

namespace WrapTuneMacOS.Signing.Tests;

/// <summary>
/// The adapter seam: WrapTune's UI-facing <see cref="SigningOptions"/> must map
/// correctly onto the MacSign engine's options. These run offline — no certs, no
/// network, no external tools.
/// </summary>
public sealed class OptionMappingTests
{
    [Fact]
    public void Pfx_mode_maps_path_and_secret_through()
    {
        var mapped = PayloadSigner.MapOptions(new SigningOptions
        {
            CertMode = CertMode.Pfx,
            PfxPath = "/certs/test.pfx",
            Secret = "p w d",          // passwords may contain spaces — must pass verbatim
            Description = "My App",
            Url = "https://example.test",
        }, accessToken: null);

        Assert.Equal(EngineCertMode.Pfx, mapped.CertMode);
        Assert.Equal("/certs/test.pfx", mapped.PfxPath);
        Assert.Equal("p w d", mapped.Secret);
        Assert.Equal("My App", mapped.Description);
        Assert.Equal("https://example.test", mapped.Url);
        Assert.Null(mapped.TrustedSigningAccessToken);
        Assert.Null(mapped.TimestampUrl);   // blank = skip timestamping outside Trusted Signing
    }

    [Fact]
    public void Pkcs11_mode_maps_module_pin_and_blank_thumbprint_to_null()
    {
        var mapped = PayloadSigner.MapOptions(new SigningOptions
        {
            CertMode = CertMode.Pkcs11,
            Pkcs11ModulePath = "/usr/local/lib/token.dylib",
            Pkcs11CertThumbprint = "   ",
            Secret = "1234",
        }, accessToken: null);

        Assert.Equal(EngineCertMode.Pkcs11, mapped.CertMode);
        Assert.Equal("/usr/local/lib/token.dylib", mapped.Pkcs11ModulePath);
        Assert.Null(mapped.Pkcs11CertThumbprint);
        Assert.Equal("1234", mapped.Secret);
    }

    [Fact]
    public void Trusted_signing_token_travels_as_access_token_not_secret()
    {
        // In TS mode WrapTune's Secret field holds the (optional) pasted Azure token.
        // It must reach the engine as TrustedSigningAccessToken — never as Secret,
        // which the engine would treat as a PFX password.
        var mapped = PayloadSigner.MapOptions(new SigningOptions
        {
            CertMode = CertMode.TrustedSigning,
            TrustedSigningEndpoint = "eus.codesigning.azure.net",
            TrustedSigningAccount = "acct",
            TrustedSigningProfile = "profile",
            Secret = "pasted-token",
        }, accessToken: "resolved-token");

        Assert.Equal(EngineCertMode.TrustedSigning, mapped.CertMode);
        Assert.Equal("eus.codesigning.azure.net", mapped.TrustedSigningEndpoint);
        Assert.Equal("acct", mapped.TrustedSigningAccount);
        Assert.Equal("profile", mapped.TrustedSigningProfile);
        Assert.Equal("resolved-token", mapped.TrustedSigningAccessToken);
        Assert.Null(mapped.Secret);
    }

    [Fact]
    public void Trusted_signing_defaults_to_the_microsoft_tsa_when_timestamp_is_blank()
    {
        // Trusted Signing certs are short-lived — an untimestamped signature dies
        // with the cert, so blank must mean "Microsoft TSA", never "skip".
        var blank = new SigningOptions { CertMode = CertMode.TrustedSigning, TimestampUrl = "  " };
        Assert.Equal(PayloadSigner.DefaultTrustedSigningTimestampUrl, PayloadSigner.ResolveTimestampUrl(blank));

        var custom = new SigningOptions { CertMode = CertMode.TrustedSigning, TimestampUrl = "http://tsa.example.test" };
        Assert.Equal("http://tsa.example.test", PayloadSigner.ResolveTimestampUrl(custom));

        var pfxBlank = new SigningOptions { CertMode = CertMode.Pfx, TimestampUrl = "" };
        Assert.Null(PayloadSigner.ResolveTimestampUrl(pfxBlank));
    }

    [Fact]
    public void Rbac_hint_is_appended_only_for_403s_in_trusted_signing_mode()
    {
        const string role = "Artifact Signing Certificate Profile Signer";

        Assert.Contains(role, PayloadSigner.WithRbacHint(CertMode.TrustedSigning, "HTTP 403 from endpoint"));
        Assert.Contains(role, PayloadSigner.WithRbacHint(CertMode.TrustedSigning, "Forbidden"));
        Assert.DoesNotContain(role, PayloadSigner.WithRbacHint(CertMode.TrustedSigning, "HTTP 401 Unauthorized"));
        Assert.DoesNotContain(role, PayloadSigner.WithRbacHint(CertMode.Pfx, "HTTP 403 from somewhere"));
    }

    [Fact]
    public void TryCreate_rejects_a_missing_pfx_with_an_actionable_error()
    {
        var signer = PayloadSigner.TryCreate(new SigningOptions
        {
            CertMode = CertMode.Pfx,
            PfxPath = "/definitely/not/here.pfx",
        }, out var error);

        Assert.Null(signer);
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCreate_rejects_trusted_signing_without_account_fields()
    {
        var signer = PayloadSigner.TryCreate(new SigningOptions
        {
            CertMode = CertMode.TrustedSigning,
            TrustedSigningEndpoint = "eus.codesigning.azure.net",
            // account + profile missing
        }, out var error);

        Assert.Null(signer);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryCreate_accepts_trusted_signing_with_all_fields_proving_backends_registered()
    {
        // Would fail with "support isn't loaded" if the static-ctor backend
        // registration (Msi/Pkcs11/Azure) ever regressed.
        var signer = PayloadSigner.TryCreate(new SigningOptions
        {
            CertMode = CertMode.TrustedSigning,
            TrustedSigningEndpoint = "eus.codesigning.azure.net",
            TrustedSigningAccount = "acct",
            TrustedSigningProfile = "profile",
        }, out var error);

        Assert.NotNull(signer);
        Assert.Null(error);
    }
}
