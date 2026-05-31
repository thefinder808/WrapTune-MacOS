using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

public sealed class SignerLocatorTests
{
    [Fact]
    public void Override_path_is_honored_when_it_exists()
    {
        // Any existing executable file stands in for osslsigncode here.
        var existing = "/bin/sh";
        if (!File.Exists(existing)) return;   // non-Unix CI — skip.
        Assert.Equal(existing, SignerLocator.Locate(existing));
    }

    [Fact]
    public void Nonexistent_override_falls_through_not_returned()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N"));
        Assert.NotEqual(bogus, SignerLocator.Locate(bogus));
    }

    [Fact]
    public void Install_hint_names_the_homebrew_formula()
    {
        Assert.Contains("brew install osslsigncode", SignerLocator.InstallHint);
    }

    [Fact]
    public void Jsign_override_path_is_honored_when_it_exists()
    {
        var existing = "/bin/sh";
        if (!File.Exists(existing)) return;   // non-Unix CI — skip.
        Assert.Equal(existing, SignerLocator.LocateJsign(existing));
    }

    [Fact]
    public void Jsign_install_hint_names_the_homebrew_formula()
    {
        Assert.Contains("brew install jsign", SignerLocator.JsignInstallHint);
    }

    [Fact]
    public void Azure_cli_hint_mentions_az_login()
    {
        Assert.Contains("az login", SignerLocator.AzureCliHint);
    }
}
