namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// Differential tests against a known-good <c>.intunewin</c> produced by the
/// OFFICIAL Content Prep Tool on Windows. These are the authoritative offline
/// cross-check: if our reader validates an official package, our writer (the
/// exact inverse) is producing the same structure.
///
/// They no-op until fixtures are committed under <c>Fixtures/golden/</c> (see
/// Fixtures/README.md). xUnit 2.x has no dynamic skip, so absence = early return.
/// </summary>
public sealed class GoldenFixtureTests
{
    private static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "golden");

    private static string? FindOfficialPackage() =>
        Directory.Exists(GoldenDir)
            ? Directory.EnumerateFiles(GoldenDir, "*.intunewin", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;

    [Fact]
    public void Official_package_decrypts_and_validates_under_our_reader()
    {
        var official = FindOfficialPackage();
        if (official is null) return;   // No golden fixture committed yet — see Fixtures/README.md.

        var contents = IntuneWinReader.Read(official);
        Assert.True(contents.MacValid, "Our HMAC check must accept an official package.");
        Assert.True(contents.DigestValid, "Our FileDigest check must accept an official package.");
        Assert.True(contents.SizeValid, "Our size check must accept an official package.");
    }

    [Fact]
    public async Task Our_output_payload_matches_official_for_the_same_source()
    {
        var official = FindOfficialPackage();
        var sourceDir = Path.Combine(GoldenDir, "source");
        if (official is null || !Directory.Exists(sourceDir)) return;   // Needs both fixtures.

        // Recover the official payload's file tree.
        var officialTree = TestWorkspace.ReadZipEntries(IntuneWinReader.Read(official).DecryptedZip);

        // Package the same source with our engine and recover its payload tree.
        var setup = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .First(InstallerExtensions.IsInstaller);
        using var ws = new TestWorkspace();
        var result = await new IntuneWinWriter()
            .PackageAsync(new PackageRequest(sourceDir, setup, ws.Output, Overwrite: true));
        Assert.True(result.Success, result.Error);
        var ourTree = TestWorkspace.ReadZipEntries(IntuneWinReader.Read(result.OutputPath!).DecryptedZip);

        // Compare extracted contents (not raw ZIP bytes — zip metadata/ordering
        // differs across implementations; the recovered FILES must match).
        Assert.Equal(
            officialTree.OrderBy(k => k.Key).Select(k => k.Key),
            ourTree.OrderBy(k => k.Key).Select(k => k.Key));
        foreach (var (name, bytes) in officialTree)
            Assert.Equal(bytes, ourTree[name]);
    }
}
