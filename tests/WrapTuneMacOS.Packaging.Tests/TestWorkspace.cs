using System.IO.Compression;

namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>A throwaway temp directory with a source-folder builder, auto-deleted on dispose.</summary>
internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; }
    public string Source { get; }
    public string Output { get; }

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "wraptune-test-" + Guid.NewGuid().ToString("N"));
        Source = Path.Combine(Root, "source");
        Output = Path.Combine(Root, "output");
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Output);
    }

    /// <summary>Write a file (relative path under the source folder) and return its full path.</summary>
    public string AddSourceFile(string relativePath, byte[] content)
    {
        var full = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string AddSourceFile(string relativePath, string content) =>
        AddSourceFile(relativePath, System.Text.Encoding.UTF8.GetBytes(content));

    /// <summary>Read a ZIP byte array into a name→bytes map (forward-slash entry names).</summary>
    public static Dictionary<string, byte[]> ReadZipEntries(byte[] zipBytes)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;   // directory marker
            using var s = e.Open();
            using var buf = new MemoryStream();
            s.CopyTo(buf);
            map[e.FullName] = buf.ToArray();
        }
        return map;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}
