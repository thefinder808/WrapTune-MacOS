using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

public sealed class SignableExtensionsTests
{
    [Theory]
    [InlineData("setup.exe")]
    [InlineData("App.MSI")]          // case-insensitive
    [InlineData("install.ps1")]
    [InlineData("helper.dll")]
    [InlineData("driver.sys")]
    public void Signable_types_are_recognized(string name) =>
        Assert.True(SignableExtensions.IsSignable(name));

    [Theory]
    [InlineData("install.cmd")]      // shell scripts are NOT Authenticode-signable
    [InlineData("run.bat")]
    [InlineData("readme.txt")]
    [InlineData("noext")]
    public void Unsignable_types_are_rejected(string name) =>
        Assert.False(SignableExtensions.IsSignable(name));

    [Fact]
    public void Cmd_and_bat_are_excluded_even_though_they_are_valid_setup_files()
    {
        // They're accepted as setup files by InstallerExtensions, but Authenticode
        // can't sign them — guard against that divergence regressing.
        Assert.DoesNotContain(".cmd", SignableExtensions.All);
        Assert.DoesNotContain(".bat", SignableExtensions.All);
    }
}
