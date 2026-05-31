namespace WrapTuneMacOS.Signing;

/// <summary>
/// Resolves the short-lived Azure access token jsign needs for Trusted Signing.
/// Prefers a manually-supplied token (transient, never persisted); otherwise fetches
/// a fresh one from the Azure CLI. Trusted Signing tokens expire in ~1 hour, so we
/// fetch one per signing run rather than caching.
/// </summary>
internal static class AzureTokenProvider
{
    /// <summary>The Trusted Signing resource the token must be scoped to.</summary>
    public const string Resource = "https://codesigning.azure.net";

    /// <summary>
    /// Returns <c>(token, null)</c> on success, or <c>(null, error)</c> on failure.
    /// Uses <paramref name="manualToken"/> if provided; else shells out to
    /// <paramref name="azPath"/> (the Azure CLI). A null <paramref name="azPath"/>
    /// with no manual token is an error.
    /// </summary>
    public static async Task<(string? Token, string? Error)> TryGetTokenAsync(
        string? manualToken, string? azPath, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(manualToken))
            return (manualToken.Trim(), null);

        if (azPath is null)
            return (null, SignerLocator.AzureCliHint);

        try
        {
            var (exit, stdout, stderr) = await ProcessRunner.RunAsync(
                azPath,
                ["account", "get-access-token", "--resource", Resource, "--query", "accessToken", "-o", "tsv"],
                ct);

            var token = stdout.Trim();
            if (exit != 0 || token.Length == 0)
                return (null, $"Azure CLI could not get a token ({FirstLine(stderr)}). Run `az login`, or paste a token.");

            return (token, null);
        }
        catch (Exception ex)
        {
            return (null, "Azure CLI failed: " + ex.Message);
        }
    }

    private static string FirstLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no detail";
}
