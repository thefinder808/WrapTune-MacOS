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
    public static int? EncryptPercent(string line) => PercentAfter("Encrypting… ", line);

    /// <summary>The same for the zip stage's "Zipping… NN%" lines.</summary>
    public static int? ZipPercent(string line) => PercentAfter("Zipping… ", line);

    private static int? PercentAfter(string prefix, string line)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith('%')) return null;
        var digits = line[prefix.Length..^1];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var pct) ? pct : null;
    }

    /// <summary>Setup-file names every PSADT package shares. Naming the output
    /// after one of these makes unrelated packages replace each other.</summary>
    private static readonly string[] SharedSetupNames = ["Invoke-AppDeployToolkit", "Deploy-Application"];

    /// <summary>The name the output-name field is pre-filled with: the setup
    /// file's name (what the official tool uses), or the source folder's name
    /// when the setup file is a PSADT launcher. Empty until a setup file is set.</summary>
    public static string DefaultOutputName(string? sourceFolder, string? setupFile)
    {
        if (string.IsNullOrWhiteSpace(setupFile)) return "";
        var setupName = Path.GetFileNameWithoutExtension(setupFile.Trim());
        if (!SharedSetupNames.Contains(setupName, StringComparer.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(sourceFolder))
            return setupName;

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceFolder.Trim()));
        return folderName.Length > 0 ? folderName : setupName;
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

    /// <summary>The installers found under a source folder, as paths relative to
    /// it, plus how many more there were beyond the cap.</summary>
    public sealed record InstallerList(IReadOnlyList<string> RelativePaths, int Omitted);

    /// <summary>
    /// The setup file a source folder most likely wants, or null when it's
    /// genuinely ambiguous. A PSADT launcher wins outright — those folders always
    /// hold the .exe and its .ps1 side by side, plus helpers. Otherwise it's the
    /// folder's only installer, if it has exactly one. Top level only: a setup
    /// file buried in a subfolder is never the obvious choice.
    /// </summary>
    public static string? GuessSetupFile(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        var installers = Directory.GetFiles(folder)
            .Where(f => InstallerExtensions.IsInstaller(f) && !IntuneWinWriter.IsMacMetadataFile(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var launcher in SharedSetupNames)
        {
            var hit = installers.FirstOrDefault(f =>
                Path.GetFileName(f).Equals(launcher + ".exe", StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return installers.Length == 1 ? installers[0] : null;
    }

    /// <summary>Every installer under <paramref name="folder"/>, nearest the top
    /// first, for the setup-file picker. Capped — a folder with hundreds of
    /// .exe files needs the file dialog, not a menu.</summary>
    public static InstallerList FindInstallers(string? folder, int max = 15)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new InstallerList([], 0);
        try
        {
            var root = Path.GetFullPath(folder);
            var all = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => InstallerExtensions.IsInstaller(f) && !IntuneWinWriter.IsMacMetadataFile(f))
                .Select(f => Path.GetRelativePath(root, f))
                .OrderBy(r => r.Count(ch => ch == Path.DirectorySeparatorChar))
                .ThenBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new InstallerList(all.Take(max).ToList(), Math.Max(0, all.Count - max));
        }
        catch
        {
            return new InstallerList([], 0);   // unreadable folder — fall back to the file dialog
        }
    }

    /// <summary>Footer text while something is dragged over the window: says
    /// what letting go will do, before it's done.</summary>
    public static string DropHint(DropKind kind) => kind switch
    {
        DropKind.SourceFolder => "drop to use as the source folder",
        DropKind.OutputFolder => "drop to use as the output folder",
        DropKind.SetupFile => "drop to use as the setup file",
        DropKind.InspectPackage => "drop to inspect this package",
        _ => "",
    };

    /// <summary>The line under the file name on the done screen:
    /// "39.9 MB · built in 00:04 · signed · pfx".</summary>
    public static string ReceiptSummary(long packageBytes, TimeSpan elapsed, string? signedChip)
    {
        var summary = $"{FormatSize(packageBytes)} · built in {FormatElapsed(elapsed)}";
        return string.IsNullOrWhiteSpace(signedChip) ? summary : $"{summary} · {signedChip}";
    }

    /// <summary>A path as the form shows it: the home folder shortened to "~".
    /// <paramref name="home"/> is a parameter so this stays testable.</summary>
    public static string AbbreviateHome(string? path, string? home)
    {
        path = path?.Trim() ?? "";
        home = string.IsNullOrEmpty(home) ? null : Path.TrimEndingDirectorySeparator(home);
        if (home is null || !path.StartsWith(home, StringComparison.Ordinal)) return path;

        // "/Users/sam" must not abbreviate "/Users/samantha/…".
        var rest = path[home.Length..];
        return rest.Length == 0 || rest[0] == Path.DirectorySeparatorChar || rest[0] == Path.AltDirectorySeparatorChar
            ? "~" + rest
            : path;
    }

    /// <summary>The setup file as the form shows it: relative to the source
    /// folder when it lives there (the usual case — the folder is already on the
    /// row above), otherwise the full path with the home folder shortened.</summary>
    public static string SetupDisplay(string? sourceFolder, string? setupFile, string? home)
    {
        var setup = setupFile?.Trim() ?? "";
        var source = sourceFolder?.Trim() ?? "";
        if (setup.Length == 0) return "";
        try
        {
            if (source.Length > 0 && IntuneWinWriter.IsInsideFolder(source, setup))
                return Path.GetRelativePath(Path.GetFullPath(source), Path.GetFullPath(setup));
        }
        catch
        {
            // Half-typed or malformed path — fall through to showing it as typed.
        }
        return AbbreviateHome(setup, home);
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
