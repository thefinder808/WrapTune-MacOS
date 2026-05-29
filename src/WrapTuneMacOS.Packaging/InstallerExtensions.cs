namespace WrapTuneMacOS.Packaging;

/// <summary>
/// The setup-file extensions WrapTune MacOS accepts. Single source of truth —
/// mirrors WrapTune's <c>_installerExtensions</c>. Used by the UI's file picker
/// and the source-folder auto-detect.
/// </summary>
public static class InstallerExtensions
{
    public static readonly IReadOnlyList<string> All =
        [".exe", ".msi", ".ps1", ".cmd", ".bat"];

    /// <summary>True if <paramref name="path"/> has an accepted setup-file extension.</summary>
    public static bool IsInstaller(string path) =>
        All.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
