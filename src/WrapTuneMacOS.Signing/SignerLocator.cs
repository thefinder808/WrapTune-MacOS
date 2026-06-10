namespace WrapTuneMacOS.Signing;

/// <summary>
/// Locates the Azure CLI (<c>az</c>), used only to auto-fetch Trusted Signing access
/// tokens. Signing itself runs in process via the MacSign engine, so no other
/// external tools are needed. The explicit Homebrew-directory probe matters because a
/// Finder-launched app only inherits the minimal launchd <c>PATH</c>, which hides
/// Homebrew installs.
/// </summary>
public static class SignerLocator
{
    /// <summary>Actionable message shown when no Azure token source is available.</summary>
    public const string AzureCliHint =
        "Azure CLI not found or not logged in — run `az login`, or paste an access token.";

    /// <summary>Common Homebrew bin directories: Apple-silicon first, then Intel.</summary>
    private static readonly string[] BinDirs = ["/opt/homebrew/bin", "/usr/local/bin"];

    /// <summary>Resolve the Azure CLI (<c>az</c>) path. Returns null if not installed.</summary>
    public static string? LocateAzureCli() => LocateBinary("az");

    private static string? LocateBinary(string name)
    {
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
}
