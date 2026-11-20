using WrapTuneMacOS.Packaging;

namespace WrapTuneMacOS.Services;

/// <summary>One folder waiting to be packaged in a batch.</summary>
public sealed class QueueItem
{
    /// <summary>The source folder (full path).</summary>
    public required string Folder { get; init; }

    /// <summary>The setup file inside <see cref="Folder"/>; null until one is
    /// guessed or chosen — the batch can't run while any item has none.</summary>
    public string? SetupFile { get; set; }

    /// <summary>The package's file name, without ".intunewin". Unique within the queue.</summary>
    public required string OutputName { get; init; }

    public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder));
}

/// <summary>How one queued folder turned out. Exactly one of
/// <paramref name="OutputPath"/> / <paramref name="Error"/> is set, or neither
/// when the item never ran (the batch was cancelled before reaching it).</summary>
public sealed record QueueResult(QueueItem Item, string? OutputPath, string? Error)
{
    public bool Success => OutputPath is not null;
    public bool Skipped => OutputPath is null && Error is null;
}

/// <summary>
/// Pure logic behind batch packaging: turning dropped folders into queue items
/// and wording the queue's status. UI-free so it's testable offline; the
/// engine is untouched — a batch is the single-package run, repeated.
/// </summary>
public static class BatchQueue
{
    /// <summary>
    /// Queue items for <paramref name="folders"/>, to be appended to
    /// <paramref name="existing"/>. Anything that isn't a folder, or is already
    /// queued, is left out. Each package is named after its folder — the one
    /// name that is distinct by construction when the setup files all share a
    /// name (the PSADT case this exists for) — with " (2)", " (3)"… appended
    /// if two folders from different places share a name too.
    /// </summary>
    public static List<QueueItem> Plan(IEnumerable<string> folders, IReadOnlyList<QueueItem>? existing = null)
    {
        existing ??= [];
        var seenFolders = new HashSet<string>(existing.Select(i => i.Folder), StringComparer.OrdinalIgnoreCase);
        var takenNames = new HashSet<string>(existing.Select(i => i.OutputName), StringComparer.OrdinalIgnoreCase);
        var added = new List<QueueItem>();

        foreach (var raw in folders)
        {
            if (string.IsNullOrWhiteSpace(raw) || !Directory.Exists(raw)) continue;
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            if (!seenFolders.Add(folder)) continue;

            var baseName = Path.GetFileName(folder);
            if (baseName.Length == 0 || IntuneWinWriter.OutputNameError(baseName) is not null)
                baseName = "package";   // a volume root, or a folder name Windows wouldn't accept

            var name = baseName;
            for (int n = 2; !takenNames.Add(name); n++)
                name = $"{baseName} ({n})";

            added.Add(new QueueItem
            {
                Folder = folder,
                SetupFile = PackagingFlow.GuessSetupFile(folder),
                OutputName = name,
            });
        }
        return added;
    }

    /// <summary>Why <paramref name="item"/> can't be packaged yet, or null when it can.</summary>
    public static string? Problem(QueueItem item)
    {
        if (string.IsNullOrWhiteSpace(item.SetupFile)) return "choose a setup file";
        if (!File.Exists(item.SetupFile)) return "setup file is missing";
        if (!IntuneWinWriter.IsInsideFolder(item.Folder, item.SetupFile)) return "setup file is outside the folder";
        return null;
    }

    /// <summary>Step-1 header summary while a queue is showing.</summary>
    public static string Summary(int count) =>
        $"{count} package{(count == 1 ? "" : "s")} · each named after its folder";

    /// <summary>Footer status for a queue that's ready to run.</summary>
    public static string ReadyStatus(int count, bool signingOn)
    {
        var packages = $"{count} package{(count == 1 ? "" : "s")}";
        return signingOn ? $"ready — sign, then wrap {packages}" : $"ready — {packages}";
    }

    /// <summary>The done screen's title: "Packaged 3", or "Packaged 2 of 3"
    /// when some failed or never ran.</summary>
    public static string ResultTitle(IReadOnlyList<QueueResult> results)
    {
        var ok = results.Count(r => r.Success);
        if (ok == 0) return "Nothing packaged";
        return ok == results.Count ? $"Packaged {ok}" : $"Packaged {ok} of {results.Count}";
    }

    /// <summary>The done screen's footer line.</summary>
    public static string ResultStatus(IReadOnlyList<QueueResult> results)
    {
        var ok = results.Count(r => r.Success);
        var failed = results.Count(r => r.Error is not null);
        var skipped = results.Count(r => r.Skipped);
        if (failed == 0 && skipped == 0) return $"done — {ok} package{(ok == 1 ? "" : "s")} created";

        var parts = new List<string> { $"{ok} created" };
        if (failed > 0) parts.Add($"{failed} failed");
        if (skipped > 0) parts.Add($"{skipped} not run");
        return string.Join(" · ", parts);
    }
}
