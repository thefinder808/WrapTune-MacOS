namespace WrapTuneMacOS.Packaging;

/// <summary>
/// Inputs to build one <c>.intunewin</c> package.
/// </summary>
/// <param name="SourceFolder">Folder whose entire contents are packaged (the official tool's <c>-c</c>).</param>
/// <param name="SetupFile">The installer within <paramref name="SourceFolder"/> (the official tool's <c>-s</c>).</param>
/// <param name="OutputFolder">Where the resulting <c>.intunewin</c> is written (the official tool's <c>-o</c>).</param>
/// <param name="Overwrite">Overwrite an existing output file (the official tool's <c>-q</c>).</param>
public sealed record PackageRequest(
    string SourceFolder,
    string SetupFile,
    string OutputFolder,
    bool Overwrite);
