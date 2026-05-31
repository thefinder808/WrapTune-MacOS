namespace WrapTuneMacOS.Signing;

/// <summary>
/// Locates the user-installed signing tools — <c>osslsigncode</c> (local PFX/PKCS#11
/// certs + signature verification), <c>jsign</c> (Azure Trusted Signing), and the
/// Azure CLI (<c>az</c>, for fetching Trusted Signing tokens). WrapTune deliberately
/// does NOT bundle any of these — that keeps the notarized app dependency-clean (no
/// bundled native crypto / JVM, no CVE-patching burden) and lets the signers stay
/// independently auditable. The user installs them via Homebrew as needed.
/// </summary>
public static class SignerLocator
{
    /// <summary>Actionable message shown when osslsigncode can't be found.</summary>
    public const string InstallHint =
        "osslsigncode was not found. Install it with:  brew install osslsigncode";

    /// <summary>Actionable message shown when jsign can't be found.</summary>
    public const string JsignInstallHint =
        "jsign was not found. Install it with:  brew install jsign";

    /// <summary>Actionable message shown when no Azure token source is available.</summary>
    public const string AzureCliHint =
        "Azure CLI not found or not logged in — run `az login`, or paste an access token.";

    /// <summary>Common Homebrew bin directories: Apple-silicon first, then Intel.</summary>
    private static readonly string[] BinDirs = ["/opt/homebrew/bin", "/usr/local/bin"];

    /// <summary>
    /// Resolve the osslsigncode path. Order: explicit <paramref name="overridePath"/>
    /// → known Homebrew locations → <c>PATH</c>. Returns null if none exists.
    /// </summary>
    public static string? Locate(string? overridePath = null) => LocateBinary("osslsigncode", overridePath);

    /// <summary>Resolve the jsign path (Homebrew / PATH), honoring an optional override.</summary>
    public static string? LocateJsign(string? overridePath = null) => LocateBinary("jsign", overridePath);

    /// <summary>Resolve the Azure CLI (<c>az</c>) path.</summary>
    public static string? LocateAzureCli() => LocateBinary("az", null);

    private static string? LocateBinary(string name, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        foreach (var dir in BinDirs)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }

        return FindOnPath(name);
    }

    private static string? FindOnPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Run <c>osslsigncode --version</c> for a "check signer" probe. Returns the
    /// first non-empty output line, or null if the tool is missing or errors.
    /// </summary>
    public static async Task<string?> TryGetVersionAsync(string? overridePath = null, CancellationToken ct = default)
    {
        var exe = Locate(overridePath);
        if (exe is null) return null;
        try
        {
            var (exit, stdout, stderr) = await ProcessRunner.RunAsync(exe, ["--version"], ct);
            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return exit == 0
                ? text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
