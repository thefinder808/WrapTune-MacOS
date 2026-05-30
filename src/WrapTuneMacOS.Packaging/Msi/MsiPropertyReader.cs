namespace WrapTuneMacOS.Packaging.Msi;

/// <summary>
/// Extracts the MSI metadata Intune records in Detection.xml — pure managed, no
/// Windows Installer APIs. Returns null on any read failure; callers must treat
/// null as "no metadata available" and never substitute guessed values.
/// </summary>
public static class MsiPropertyReader
{
    /// <summary>
    /// The install context Intune derives from an MSI, encoded the way the
    /// official tool / Microsoft Graph do (<c>win32LobAppMsiPackageType</c>:
    /// perMachine=0, perUser=1, dualPurpose=2).
    /// </summary>
    internal readonly record struct InstallContext(int ExecutionContext, bool IsMachineInstall, bool IsUserInstall);

    public static MsiInfo? TryRead(string msiPath)
    {
        try
        {
            var db = new MsiDatabase(File.ReadAllBytes(msiPath));
            var props = db.ReadProperties();
            if (props.Count == 0) return null;

            string? Get(string key) => props.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

            // Install context comes from ALLUSERS (per Microsoft's "Installation
            // Context" docs), not a fixed assumption. "1" ⇒ per-machine, "2" ⇒
            // dual-purpose, empty/absent ⇒ per-user.
            var ctx = ResolveInstallContext(props.TryGetValue("ALLUSERS", out var all) ? all : null);

            return new MsiInfo
            {
                MsiProductCode = Get("ProductCode"),
                MsiProductVersion = Get("ProductVersion"),
                MsiUpgradeCode = Get("UpgradeCode"),
                MsiPublisher = Get("Manufacturer"),
                MsiPackageCode = db.ReadPackageCode(),
                MsiExecutionContext = ctx.ExecutionContext,
                MsiIsMachineInstall = ctx.IsMachineInstall,
                MsiIsUserInstall = ctx.IsUserInstall,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Map an MSI's <c>ALLUSERS</c> value to the install context Intune records.
    /// Windows Installer semantics: <c>ALLUSERS=1</c> is per-machine; <c>2</c> is
    /// a dual-purpose package (installable per-machine or per-user); anything
    /// else — including empty or an absent property — is per-user.
    /// </summary>
    internal static InstallContext ResolveInstallContext(string? allUsers) =>
        (allUsers?.Trim()) switch
        {
            "1" => new InstallContext(ExecutionContext: 0, IsMachineInstall: true, IsUserInstall: false),
            "2" => new InstallContext(ExecutionContext: 2, IsMachineInstall: true, IsUserInstall: true),
            _ => new InstallContext(ExecutionContext: 1, IsMachineInstall: false, IsUserInstall: true),
        };
}
