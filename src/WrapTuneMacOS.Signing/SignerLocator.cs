namespace WrapTuneMacOS.Signing;

/// <summary>
/// Locates the user-installed <c>osslsigncode</c> binary. WrapTune deliberately
/// does NOT bundle it — that keeps the notarized app dependency-clean (no bundled
/// native crypto / OpenSSL, no CVE-patching burden) and lets the signer stay
/// independently auditable. The user installs it once via Homebrew.
/// </summary>
public static class SignerLocator
{
    /// <summary>Actionable message shown when the binary can't be found.</summary>
    public const string InstallHint =
        "osslsigncode was not found. Install it with:  brew install osslsigncode";

    /// <summary>Common Homebrew locations: Apple-silicon first, then Intel.</summary>
    private static readonly string[] CommonPaths =
    [
        "/opt/homebrew/bin/osslsigncode",
        "/usr/local/bin/osslsigncode",
    ];

    /// <summary>
    /// Resolve the osslsigncode path. Order: explicit <paramref name="overridePath"/>
    /// → known Homebrew locations → <c>PATH</c>. Returns null if none exists.
    /// </summary>
    public static string? Locate(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        foreach (var p in CommonPaths)
            if (File.Exists(p)) return p;

        return FindOnPath();
    }

    private static string? FindOnPath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, "osslsigncode");
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
