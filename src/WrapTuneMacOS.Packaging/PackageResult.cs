namespace WrapTuneMacOS.Packaging;

/// <summary>Outcome of a packaging run.</summary>
/// <param name="Success">True if a valid <c>.intunewin</c> was written.</param>
/// <param name="OutputPath">Absolute path to the written package, when successful.</param>
/// <param name="Error">A human-readable message when not successful.</param>
public sealed record PackageResult(bool Success, string? OutputPath, string? Error)
{
    public static PackageResult Ok(string outputPath) => new(true, outputPath, null);
    public static PackageResult Fail(string error) => new(false, null, error);
}
