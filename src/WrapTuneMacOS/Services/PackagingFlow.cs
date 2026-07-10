using System.Globalization;
using WrapTuneMacOS.Packaging;
using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Services;

/// <summary>Where a window-wide drop should land.</summary>
public enum DropKind
{
    SourceFolder,
    OutputFolder,
    SetupFile,
    InspectPackage,
}

/// <summary>Stages shown in the packaging progress view, in execution order.
/// Note: signing runs BEFORE zipping (the payload is signed in place, then
/// wrapped) — the stage list reflects reality, not the mock's order.</summary>
public enum PackageStage
{
    Sign,
    Zip,
    Encrypt,
    MsiMetadata,
    DetectionXml,
    Assemble,
}

/// <summary>
/// Pure logic behind the flow window: maps the engine's IProgress lines onto
/// stages and produces the derived strings the design displays. Kept static and
/// UI-free so it's testable offline.
/// </summary>
public static class PackagingFlow
{
    /// <summary>The stage an engine progress line belongs to, or null for lines
    /// that only matter to the raw log. Signing lines are deliberately not
    /// mapped — the app drives the Sign stage itself around SignAsync.</summary>
    public static PackageStage? StageFor(string line)
    {
        if (line.StartsWith("Zipping", StringComparison.Ordinal)) return PackageStage.Zip;
        if (line.StartsWith("Encrypting", StringComparison.Ordinal)) return PackageStage.Encrypt;
        if (line.StartsWith("Reading MSI", StringComparison.Ordinal)) return PackageStage.MsiMetadata;
        if (line.StartsWith("MSI:", StringComparison.Ordinal)) return PackageStage.MsiMetadata;
        if (line.StartsWith("Writing Detection.xml", StringComparison.Ordinal)) return PackageStage.DetectionXml;
        if (line.StartsWith("Assembling", StringComparison.Ordinal)) return PackageStage.Assemble;
        if (line.StartsWith("Created ", StringComparison.Ordinal)) return PackageStage.Assemble;
        return null;
    }

    /// <summary>Parses the engine's chunked "Encrypting… NN%" lines so the bar
    /// can move inside the encrypt stage; null for every other line.</summary>
    public static int? EncryptPercent(string line)
    {
        const string prefix = "Encrypting… ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith('%')) return null;
        var digits = line[prefix.Length..^1];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var pct) ? pct : null;
    }

    /// <summary>Footer status for the form state.</summary>
    public static string PathsStatus(int pathsSet, bool signingOn) =>
        pathsSet >= 3
            ? signingOn ? "ready — sign, then wrap" : "ready — wrap when you are"
            : $"{pathsSet} of 3 paths set";

    public static string FormatElapsed(TimeSpan t) =>
        $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    /// <summary>"500 B" / "1.5 KB" / "38.2 MB" / "2.1 GB".</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1_000 => $"{bytes} B",
        < 1_000_000 => $"{bytes / 1_000.0:0.#} KB",
        < 1_000_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        _ => $"{bytes / 1_000_000_000.0:0.#} GB",
    };

    public static string SourceSummary(int fileCount, long totalBytes) =>
        $"{fileCount} file{(fileCount == 1 ? "" : "s")} · {FormatSize(totalBytes)}";

    /// <summary>Design behavior: a freshly chosen source folder defaults the
    /// output folder to its parent (user-editable afterwards).</summary>
    public static string? DefaultOutputFolder(string? sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder)) return null;
        try
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder)));
            return string.IsNullOrEmpty(parent) || parent == Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder))
                ? null
                : parent;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Card-footer readout for .msi setup files:
    /// "msi {ProductCode} · version · context".</summary>
    public static string MsiReadout(MsiInfo msi)
    {
        var context = msi.MsiExecutionContext switch
        {
            0 => "per-machine",
            1 => "per-user",
            2 => "dual-purpose",
            var c => $"context {c}",
        };
        return $"msi {msi.MsiProductCode} · {msi.MsiProductVersion} · {context}";
    }

    /// <summary>Window-wide drop routing: folders fill the source (or the
    /// output, when dropped straight onto its row); .intunewin files open the
    /// inspector; any other file becomes the setup file.</summary>
    public static DropKind ClassifyDrop(string path, bool isDirectory, bool overOutputRow)
    {
        if (isDirectory) return overOutputRow ? DropKind.OutputFolder : DropKind.SourceFolder;
        return path.EndsWith(".intunewin", StringComparison.OrdinalIgnoreCase)
            ? DropKind.InspectPackage
            : DropKind.SetupFile;
    }

    /// <summary>Detail line for the completed Sign stage — mode-specific, so an
    /// Azure run never shows a stale PFX filename.</summary>
    public static string SignStageDetail(CertMode mode, string? pfxPath) => mode switch
    {
        CertMode.Pkcs11 => "hsm · timestamped",
        CertMode.TrustedSigning => "azure · timestamped",
        _ => $"{Path.GetFileName(pfxPath ?? "")} · timestamped",
    };

    /// <summary>Bar fraction: completed stages plus progress inside the active
    /// one, out of the visible stage count.</summary>
    public static double OverallFraction(int totalStages, int completedStages, int? inStagePercent)
    {
        if (totalStages <= 0) return 0;
        var f = (completedStages + (inStagePercent ?? 0) / 100.0) / totalStages;
        return Math.Clamp(f, 0, 1);
    }
}
