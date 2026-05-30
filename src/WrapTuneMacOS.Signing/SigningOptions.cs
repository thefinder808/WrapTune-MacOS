namespace WrapTuneMacOS.Signing;

/// <summary>Which kind of code-signing credential to use.</summary>
public enum CertMode
{
    /// <summary>A PKCS#12 / <c>.pfx</c> file (self-signed, test, or legacy certs).</summary>
    Pfx,

    /// <summary>A PKCS#11 hardware token / HSM (modern public OV/EV certs).</summary>
    Pkcs11,
}

/// <summary>
/// Options for Authenticode-signing the payload, built from the UI. The
/// <see cref="Secret"/> (PFX password / HSM PIN) is supplied transiently for a
/// single run and is NEVER persisted to settings.
/// </summary>
public sealed record SigningOptions
{
    public CertMode CertMode { get; init; } = CertMode.Pfx;

    // ── PFX mode ──────────────────────────────────────────────────────────────
    /// <summary>Path to the <c>.pfx</c>/<c>.p12</c> file (<c>-pkcs12</c>).</summary>
    public string? PfxPath { get; init; }

    // ── PKCS#11 / HSM mode ─────────────────────────────────────────────────────
    /// <summary>Path to the PKCS#11 module (<c>-pkcs11module</c>), e.g. the vendor's <c>.dylib</c>.</summary>
    public string? Pkcs11ModulePath { get; init; }

    /// <summary>The certificate's PKCS#11 URI (<c>-pkcs11cert</c>), e.g. <c>pkcs11:token=…;object=…</c>.</summary>
    public string? Pkcs11CertUri { get; init; }

    /// <summary>The private key's PKCS#11 URI (<c>-key</c>).</summary>
    public string? KeyUri { get; init; }

    // ── Shared ─────────────────────────────────────────────────────────────────
    /// <summary>RFC3161 timestamp server URL (<c>-ts</c>). Empty/null skips timestamping.</summary>
    public string? TimestampUrl { get; init; }

    /// <summary>Signature description (<c>-n</c>). Optional.</summary>
    public string? Description { get; init; }

    /// <summary>Signature URL (<c>-i</c>). Optional.</summary>
    public string? Url { get; init; }

    /// <summary>Sign every signable file in the source folder, not just the setup file.</summary>
    public bool SignAllSignableFiles { get; init; }

    /// <summary>Explicit path to osslsigncode; null auto-detects (Homebrew / PATH).</summary>
    public string? OsslsigncodePath { get; init; }

    /// <summary>
    /// The PFX password or HSM PIN. Transient — never persisted. Supplied via a
    /// <c>0600</c> temp file (<c>-readpass</c>), never on the command line. May be
    /// null for a PIN-less token.
    /// </summary>
    public string? Secret { get; init; }
}
