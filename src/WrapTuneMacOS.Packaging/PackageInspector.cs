using System.IO.Compression;

namespace WrapTuneMacOS.Packaging;

/// <summary>What <see cref="PackageInspector.Inspect"/> learned about a package.
/// Payload details are populated only when the MAC authenticated and the
/// payload decrypted to a readable ZIP.</summary>
public sealed record PackageInspection(
    DetectionXml Detection,
    bool MacValid,
    bool DigestValid,
    bool SizeValid,
    long EncryptedSizeBytes,
    int PayloadEntryCount,
    IReadOnlyList<string> PayloadEntries)
{
    /// <summary>True when MAC, digest, and size all check out.</summary>
    public bool IsValid => MacValid && DigestValid && SizeValid;
}

/// <summary>
/// Shared inspection core behind the CLI's <c>inspect</c>/<c>extract</c>
/// commands and the GUI's Inspect Package window — one code path, two
/// frontends. Streams via <see cref="IntuneWinReader.ReadToFile"/>, so
/// multi-GB packages inspect in O(buffer) memory.
/// </summary>
public static class PackageInspector
{
    public static PackageInspection Inspect(string intuneWinPath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wraptune-inspect-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var r = IntuneWinReader.ReadToFile(intuneWinPath, tmp);

            var entries = new List<string>();
            if (File.Exists(tmp))
            {
                try
                {
                    using var zip = ZipFile.OpenRead(tmp);
                    foreach (var e in zip.Entries)
                        if (!e.FullName.EndsWith('/'))   // skip directory markers
                            entries.Add(e.FullName);
                }
                catch (InvalidDataException)
                {
                    // Decrypted bytes that aren't a readable ZIP — the digest/size
                    // flags already tell the caller the payload is suspect.
                }
            }

            return new PackageInspection(
                r.Detection, r.MacValid, r.DigestValid, r.SizeValid,
                EncryptedSizeBytes: EncryptedEntryLength(intuneWinPath),
                PayloadEntryCount: entries.Count,
                PayloadEntries: entries);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    /// <summary>Decrypt the payload ZIP to <paramref name="destZipPath"/> —
    /// a thin, discoverable alias over the streaming reader.</summary>
    public static IntuneWinFileContents ExtractPayloadZip(string intuneWinPath, string destZipPath)
        => IntuneWinReader.ReadToFile(intuneWinPath, destZipPath);

    /// <summary>Read an MSI's metadata without packaging it (the UI shows a
    /// readout as soon as an .msi setup file is picked). Null when the file
    /// isn't a parseable MSI — mirrors the engine's own tolerant behavior.</summary>
    public static MsiInfo? TryReadMsiInfo(string msiPath) => Msi.MsiPropertyReader.TryRead(msiPath);

    private static long EncryptedEntryLength(string intuneWinPath)
    {
        using var zip = ZipFile.OpenRead(intuneWinPath);
        return zip.GetEntry("IntuneWinPackage/Contents/" + IntuneWinWriter.ContentFileName)?.Length ?? 0;
    }
}
