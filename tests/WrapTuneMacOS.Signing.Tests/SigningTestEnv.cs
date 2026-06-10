using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

/// <summary>
/// Shared helpers for the signing integration tests. Signing itself runs in-process
/// (the MacSign engine), but the tests still need <c>openssl</c> (to mint a
/// throwaway PFX) and <c>osslsigncode</c> (as an INDEPENDENT verifier of the
/// engine's output — same philosophy as <c>tools/verify-intunewin.py</c>); tests
/// self-skip otherwise.
/// </summary>
internal static class SigningTestEnv
{
    public const string Password = "testpw";

    /// <summary>Common Homebrew bin directories: Apple-silicon first, then Intel.</summary>
    private static readonly string[] BinDirs = ["/opt/homebrew/bin", "/usr/local/bin"];

    /// <summary>
    /// Find osslsigncode for cross-verification. The signing lib no longer locates
    /// it (nothing shells out to it any more), so the tests do it themselves.
    /// </summary>
    public static string? LocateOsslsigncode()
    {
        foreach (var dir in BinDirs)
        {
            var candidate = Path.Combine(dir, "osslsigncode");
            if (File.Exists(candidate)) return candidate;
        }

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

    public static async Task<bool> HasOpenSslAsync()
    {
        try
        {
            var (exit, _, _) = await ProcessRunner.RunAsync("openssl", ["version"]);
            return exit == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Generate a throwaway self-signed cert and export it as a password-protected PFX. Returns the .pfx path.</summary>
    public static async Task<string> CreateSelfSignedPfxAsync(string dir)
    {
        var keyPem = Path.Combine(dir, "key.pem");
        var certPem = Path.Combine(dir, "cert.pem");
        var pfx = Path.Combine(dir, "test.pfx");

        var (rc1, _, e1) = await ProcessRunner.RunAsync("openssl",
            ["req", "-x509", "-newkey", "rsa:2048", "-keyout", keyPem, "-out", certPem,
             "-days", "2", "-nodes", "-subj", "/CN=WrapTune Signing Test"]);
        Assert.True(rc1 == 0, "openssl req failed: " + e1);

        var (rc2, _, e2) = await ProcessRunner.RunAsync("openssl",
            ["pkcs12", "-export", "-out", pfx, "-inkey", keyPem, "-in", certPem, "-passout", "pass:" + Password]);
        Assert.True(rc2 == 0, "openssl pkcs12 failed: " + e2);

        return pfx;
    }
}
