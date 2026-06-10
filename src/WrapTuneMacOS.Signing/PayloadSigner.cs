using EngineCertMode = MacSign.Signing.CertMode;
using EngineOptions = MacSign.Signing.SigningOptions;
using EngineSigner = MacSign.Signing.AuthenticodeSigner;

namespace WrapTuneMacOS.Signing;

/// <summary>
/// Authenticode-signs the Win32 payload <b>before</b> it is wrapped into a
/// <c>.intunewin</c>. This lives entirely outside <c>WrapTuneMacOS.Packaging</c>: the
/// <c>.intunewin</c> engine stays pure managed code with zero external-process calls,
/// so its supply-chain auditability is untouched. Signing only mutates the
/// <i>payload</i> files.
///
/// Signing itself runs <b>in process</b> via the MacSign engine
/// (github.com/thefinder808/macsign) — PE/.ps1/.msi formats, PFX / PKCS#11 / Azure
/// Trusted Signing credentials, RFC3161 timestamping. No external signing tools are
/// needed. The only remaining shell-out is the optional Azure CLI token fetch in
/// Trusted Signing mode (<see cref="AzureTokenProvider"/>). Files that already carry
/// a signature are skipped (in-process verify, all modes) so vendor-signed installers
/// are never clobbered.
/// </summary>
public sealed class PayloadSigner
{
    /// <summary>
    /// Microsoft's TSA, used in Trusted Signing mode when the user leaves the
    /// timestamp URL empty. Trusted Signing certs are short-lived, so an
    /// untimestamped signature would die with the cert — never skip timestamping.
    /// </summary>
    public const string DefaultTrustedSigningTimestampUrl = "http://timestamp.acs.microsoft.com";

    private readonly string? _azureCli;   // token auto-fetch (Trusted Signing)

    static PayloadSigner()
    {
        // The engine's MSI format and PKCS#11/Azure credential backends live in
        // quarantined assemblies and must be hooked in once per process.
        MacSign.Signing.Msi.MsiBackend.Register();
        MacSign.Signing.Pkcs11.Pkcs11Backend.Register();
        MacSign.Signing.Azure.AzureBackend.Register();
    }

    private PayloadSigner(string? azureCli) => _azureCli = azureCli;

    /// <summary>
    /// Create a signer after validating the options for the chosen mode (the PFX
    /// exists, the Trusted Signing account fields are present, …). Returns null and
    /// sets <paramref name="error"/> when they're not usable.
    /// </summary>
    public static PayloadSigner? TryCreate(SigningOptions options, out string? error)
    {
        if (EngineSigner.TryCreate(MapOptions(options, accessToken: null), out error) is null)
            return null;

        return new PayloadSigner(
            options.CertMode == CertMode.TrustedSigning ? SignerLocator.LocateAzureCli() : null);
    }

    /// <summary>
    /// Sign the payload in place. Always signs the setup file; when
    /// <see cref="SigningOptions.SignAllSignableFiles"/> is set, also signs every
    /// other signable file under <paramref name="sourceFolder"/>. Already-signed
    /// files are skipped. Progress is streamed via <paramref name="log"/>.
    /// </summary>
    public async Task<SignResult> SignAsync(
        string sourceFolder, string setupFile, SigningOptions options,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        // Resolve the Azure token up front (pasted token or Azure CLI) and hand it to
        // the engine explicitly. Never rely on the engine's DefaultAzureCredential
        // fallback: launched from Finder/Dock the app only has the minimal launchd
        // PATH, where the credential chain's own `az` probe can't find a Homebrew az.
        string? accessToken = null;
        if (options.CertMode == CertMode.TrustedSigning)
        {
            var (token, tokenError) = await AzureTokenProvider.TryGetTokenAsync(options.Secret, _azureCli, ct);
            if (token is null) return SignResult.Fail(tokenError!);
            accessToken = token;
        }

        var engineOptions = MapOptions(options, accessToken);
        var signer = EngineSigner.TryCreate(engineOptions, out var error);
        if (signer is null) return SignResult.Fail(error!);

        var result = await signer.SignAsync(sourceFolder, setupFile, engineOptions, log, ct);
        return result.Success
            ? SignResult.Ok()
            : SignResult.Fail(WithRbacHint(options.CertMode, result.Error!));
    }

    /// <summary>
    /// Map WrapTune's UI-facing options onto the engine's. Internal + static so the
    /// mapping is unit-testable offline. In Trusted Signing mode <c>Secret</c> is the
    /// (optional) pasted Azure token, not a credential secret — it travels via
    /// <paramref name="accessToken"/> instead.
    /// </summary>
    internal static EngineOptions MapOptions(SigningOptions o, string? accessToken) => new()
    {
        CertMode = o.CertMode switch
        {
            CertMode.Pkcs11 => EngineCertMode.Pkcs11,
            CertMode.TrustedSigning => EngineCertMode.TrustedSigning,
            _ => EngineCertMode.Pfx,
        },
        PfxPath = o.PfxPath,
        Pkcs11ModulePath = o.Pkcs11ModulePath,
        Pkcs11CertThumbprint = NullIfBlank(o.Pkcs11CertThumbprint),
        TrustedSigningEndpoint = o.TrustedSigningEndpoint,
        TrustedSigningAccount = o.TrustedSigningAccount,
        TrustedSigningProfile = o.TrustedSigningProfile,
        TrustedSigningAccessToken = accessToken,
        TimestampUrl = ResolveTimestampUrl(o),
        Description = NullIfBlank(o.Description),
        Url = NullIfBlank(o.Url),
        SignAllSignableFiles = o.SignAllSignableFiles,
        Secret = o.CertMode == CertMode.TrustedSigning ? null : o.Secret,
    };

    /// <summary>Trusted Signing always timestamps (Microsoft TSA by default); other modes only when asked.</summary>
    internal static string? ResolveTimestampUrl(SigningOptions o) =>
        o.CertMode == CertMode.TrustedSigning && string.IsNullOrWhiteSpace(o.TimestampUrl)
            ? DefaultTrustedSigningTimestampUrl
            : NullIfBlank(o.TimestampUrl);

    /// <summary>
    /// A 403 in Trusted Signing mode means the token authenticated but the identity
    /// isn't authorized — almost always the missing signer role. Point the user
    /// straight at it.
    /// </summary>
    internal static string WithRbacHint(CertMode mode, string message)
    {
        if (mode == CertMode.TrustedSigning &&
            (message.Contains("403", StringComparison.Ordinal) ||
             message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)))
            return message + "  →  Your Azure identity likely needs the \"Artifact Signing Certificate Profile Signer\" " +
                             "role on the account (role assignments can take a few minutes to propagate).";
        return message;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
