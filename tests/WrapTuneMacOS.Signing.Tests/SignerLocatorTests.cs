using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

public sealed class SignerLocatorTests
{
    [Fact]
    public void Azure_cli_hint_mentions_az_login()
    {
        Assert.Contains("az login", SignerLocator.AzureCliHint);
    }
}
