using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

/// <summary>
/// Shared helpers for the signing integration tests. Both <c>osslsigncode</c>
/// (located via <see cref="SignerLocator"/>) and <c>openssl</c> must be present;
/// tests self-skip otherwise.
/// </summary>
internal static class SigningTestEnv
{
    public const string Password = "testpw";

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
