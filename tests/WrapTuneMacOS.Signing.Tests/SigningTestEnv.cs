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

    /// <summary>
    /// Budget for every openssl / osslsigncode call in these tests (normally well
    /// under a second). Without one, a stuck child waits forever and the CI job
    /// hangs with no failing test named — which is how the 2026-10-27 release
    /// run wedged. With one, it fails as a TimeoutException naming the tool.
    /// </summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

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
            var (exit, _, _) = await ProcessRunner.RunAsync("openssl", ["version"], default, ToolTimeout);
            return exit == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;   // not installed. A timeout is NOT caught: a hang must fail, not skip.
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
             "-days", "2", "-nodes", "-subj", "/CN=WrapTune Signing Test"],
            default, ToolTimeout);
        Assert.True(rc1 == 0, "openssl req failed: " + e1);

        var (rc2, _, e2) = await ProcessRunner.RunAsync("openssl",
            ["pkcs12", "-export", "-out", pfx, "-inkey", keyPem, "-in", certPem, "-passout", "pass:" + Password],
            default, ToolTimeout);
        Assert.True(rc2 == 0, "openssl pkcs12 failed: " + e2);

        return pfx;
    }
}
