using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

public sealed class AzureTokenProviderTests
{
    [Fact]
    public async Task Manual_token_is_returned_verbatim_without_invoking_az()
    {
        // azPath is null, but a manual token short-circuits the CLI entirely.
        var (token, error) = await AzureTokenProvider.TryGetTokenAsync("  tok-123  ", azPath: null);
        Assert.Equal("tok-123", token);   // trimmed
        Assert.Null(error);
    }

    [Fact]
    public async Task No_manual_token_and_no_azure_cli_returns_actionable_error()
    {
        var (token, error) = await AzureTokenProvider.TryGetTokenAsync(manualToken: null, azPath: null);
        Assert.Null(token);
        Assert.Equal(SignerLocator.AzureCliHint, error);
    }
}
