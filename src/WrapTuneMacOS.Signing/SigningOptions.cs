namespace WrapTuneMacOS.Signing;

/// <summary>Which kind of code-signing credential to use.</summary>
public enum CertMode
{
    /// <summary>A PKCS#12 / <c>.pfx</c> file (self-signed, test, or legacy certs).</summary>
    Pfx,

    /// <summary>A PKCS#11 hardware token / HSM (modern public OV/EV certs). The key never leaves the token.</summary>
    Pkcs11,

    /// <summary>Azure Trusted Signing (cloud HSM, currently marketed as Azure Artifact Signing). The key never leaves Azure.</summary>
    TrustedSigning,
}

/// <summary>
/// Options for Authenticode-signing the payload, built from the UI. The
/// <see cref="Secret"/> (PFX password / HSM PIN / pasted Azure token) is supplied
/// transiently for a single run and is NEVER persisted to settings.
/// </summary>
public sealed record SigningOptions
{
    public CertMode CertMode { get; init; } = CertMode.Pfx;

    // ── PFX mode ──────────────────────────────────────────────────────────────
    /// <summary>Path to the <c>.pfx</c>/<c>.p12</c> file.</summary>
    public string? PfxPath { get; init; }

    // ── PKCS#11 / HSM mode ─────────────────────────────────────────────────────
    /// <summary>Path to the PKCS#11 module, e.g. the vendor's <c>.dylib</c>/<c>.so</c>.</summary>
    public string? Pkcs11ModulePath { get; init; }

    /// <summary>
    /// Optional certificate thumbprint to disambiguate when the token holds several
    /// certificates. Leave empty when the token holds exactly one.
    /// </summary>
    public string? Pkcs11CertThumbprint { get; init; }

    // ── Azure Trusted Signing mode ─────────────────────────────────────────────
    /// <summary>The Trusted Signing endpoint, e.g. <c>eus.codesigning.azure.net</c> (scheme optional).</summary>
    public string? TrustedSigningEndpoint { get; init; }

    /// <summary>The Trusted Signing account name.</summary>
    public string? TrustedSigningAccount { get; init; }

    /// <summary>The certificate profile name to sign with.</summary>
    public string? TrustedSigningProfile { get; init; }

    // ── Shared ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// RFC3161 timestamp server URL; a comma-separated list is tried in order.
    /// Empty/null skips timestamping — except in Trusted Signing mode, where the
    /// Microsoft TSA is used by default because those certs are short-lived and an
    /// untimestamped signature would die with the cert.
    /// </summary>
    public string? TimestampUrl { get; init; }

    /// <summary>Signature description (SpcSpOpusInfo program name). Optional.</summary>
    public string? Description { get; init; }

    /// <summary>Signature URL (SpcSpOpusInfo more-info link). Optional.</summary>
    public string? Url { get; init; }

    /// <summary>Sign every signable file in the source folder, not just the setup file.</summary>
    public bool SignAllSignableFiles { get; init; }

    /// <summary>
    /// The credential secret for the chosen mode: the PFX password, the HSM PIN, or
    /// (for Trusted Signing) a manually-supplied Azure access token. Transient —
    /// never persisted, never put on a command line. May be null (password-less PFX /
    /// PIN-less token; or auto-fetch the Azure token via the CLI).
    /// </summary>
    public string? Secret { get; init; }
}
