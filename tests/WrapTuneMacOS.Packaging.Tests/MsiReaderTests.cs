using WrapTuneMacOS.Packaging.Msi;

namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// Integration test for the MSI reader against a real .msi. Self-skips when no
/// fixture is available; point it at one via the WRAPTUNE_MSI_FIXTURE env var
/// or drop a small .msi under Fixtures/msi/. When the fixture is WrapTune's own
/// MSI, it also asserts the known WiX values (publisher / upgrade code).
/// </summary>
public sealed class MsiReaderTests
{
    private const string GuidPattern =
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$";

    private static string? FindMsi()
    {
        var env = Environment.GetEnvironmentVariable("WRAPTUNE_MSI_FIXTURE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "msi");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.msi").FirstOrDefault()
            : null;
    }

    [Fact]
    public void Reads_core_properties_from_a_real_msi()
    {
        var msi = FindMsi();
        if (msi is null) return;   // no MSI fixture — skip

        var info = MsiPropertyReader.TryRead(msi);
        Assert.NotNull(info);
        Assert.Matches(GuidPattern, info!.MsiProductCode ?? "");
        Assert.Matches(GuidPattern, info.MsiUpgradeCode ?? "");
        Assert.False(string.IsNullOrWhiteSpace(info.MsiProductVersion));

        if (Path.GetFileName(msi).Equals("WrapTune.msi", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal("thefinder808", info.MsiPublisher);
            Assert.Contains("B7E4F831", info.MsiUpgradeCode ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }
}
