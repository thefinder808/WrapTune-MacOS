using WrapTuneMacOS.Packaging.Msi;

namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// Deterministic unit tests for deriving the MSI install context from the
/// <c>ALLUSERS</c> property — no fixture needed. These pin the mapping that
/// drives Detection.xml's <c>MsiExecutionContext</c> / <c>MsiIsMachineInstall</c>
/// / <c>MsiIsUserInstall</c>, whose integer encoding follows Microsoft Graph's
/// <c>win32LobAppMsiPackageType</c> enum (perMachine=0, perUser=1, dualPurpose=2).
/// </summary>
public sealed class MsiInstallContextTests
{
    [Fact]
    public void AllUsers_1_is_per_machine()
    {
        var ctx = MsiPropertyReader.ResolveInstallContext("1");
        Assert.Equal(0, ctx.ExecutionContext);
        Assert.True(ctx.IsMachineInstall);
        Assert.False(ctx.IsUserInstall);
    }

    [Fact]
    public void AllUsers_2_is_dual_purpose()
    {
        var ctx = MsiPropertyReader.ResolveInstallContext("2");
        Assert.Equal(2, ctx.ExecutionContext);
        Assert.True(ctx.IsMachineInstall);
        Assert.True(ctx.IsUserInstall);
    }

    [Theory]
    [InlineData("")]      // ALLUSERS explicitly empty ⇒ per-user
    [InlineData(null)]    // ALLUSERS absent from the Property table ⇒ per-user
    public void AllUsers_empty_or_absent_is_per_user(string? allUsers)
    {
        var ctx = MsiPropertyReader.ResolveInstallContext(allUsers);
        Assert.Equal(1, ctx.ExecutionContext);
        Assert.False(ctx.IsMachineInstall);
        Assert.True(ctx.IsUserInstall);
    }

    [Fact]
    public void AllUsers_value_is_trimmed()
    {
        // Some authoring tools pad the value; treat " 1 " as per-machine.
        var ctx = MsiPropertyReader.ResolveInstallContext(" 1 ");
        Assert.Equal(0, ctx.ExecutionContext);
        Assert.True(ctx.IsMachineInstall);
    }
}
