namespace WrapTuneMacOS.Signing;

/// <summary>Outcome of a payload-signing run.</summary>
/// <param name="Success">True if every targeted file is now signed (or was already signed).</param>
/// <param name="Error">A human-readable message when not successful.</param>
public sealed record SignResult(bool Success, string? Error)
{
    public static SignResult Ok() => new(true, null);
    public static SignResult Fail(string error) => new(false, error);
}
