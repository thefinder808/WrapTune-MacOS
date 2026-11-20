namespace WrapTuneMacOS.Packaging;

/// <summary>
/// Inputs to build one <c>.intunewin</c> package.
/// </summary>
/// <param name="SourceFolder">Folder whose entire contents are packaged (the official tool's <c>-c</c>).</param>
/// <param name="SetupFile">The installer within <paramref name="SourceFolder"/> (the official tool's <c>-s</c>).</param>
/// <param name="OutputFolder">Where the resulting <c>.intunewin</c> is written (the official tool's <c>-o</c>).</param>
/// <param name="Overwrite">Overwrite an existing output file (the official tool's <c>-q</c>).</param>
/// <param name="OutputName">Name of the output file, with or without <c>.intunewin</c>.
/// Null or blank keeps the official tool's behavior: the setup file's name. Lets
/// packages whose setup files share a name (every PSADT package) land side by side.</param>
public sealed record PackageRequest(
    string SourceFolder,
    string SetupFile,
    string OutputFolder,
    bool Overwrite,
    string? OutputName = null);
