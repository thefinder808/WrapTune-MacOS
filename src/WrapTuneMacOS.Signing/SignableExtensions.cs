namespace WrapTuneMacOS.Signing;

/// <summary>
/// The subset of accepted setup-file extensions that Authenticode can actually
/// sign: PE images (<c>.exe</c>/<c>.dll</c>/<c>.sys</c>), Windows Installer
/// databases (<c>.msi</c>), and PowerShell scripts (<c>.ps1</c>) — the same set the
/// MacSign engine implements. Plain shell scripts (<c>.cmd</c>/<c>.bat</c>) are NOT
/// Authenticode-signable, so they are deliberately excluded here even though
/// <c>WrapTuneMacOS.Packaging.InstallerExtensions</c> accepts them as setup files.
/// </summary>
public static class SignableExtensions
{
    public static readonly IReadOnlyList<string> All =
        [".exe", ".dll", ".sys", ".msi", ".ps1"];

    /// <summary>True if <paramref name="path"/> has an Authenticode-signable extension.</summary>
    public static bool IsSignable(string path) =>
        All.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
