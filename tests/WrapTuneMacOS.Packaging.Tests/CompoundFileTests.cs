using System.Buffers.Binary;
using WrapTuneMacOS.Packaging.Msi;

namespace WrapTuneMacOS.Packaging.Tests;

/// <summary>
/// Robustness tests for the OLE2/CFBF reader against malformed input. The
/// reader parses arbitrary vendor-supplied MSIs, so a corrupt or crafted file
/// must degrade gracefully — never hang or exhaust memory.
/// </summary>
public sealed class CompoundFileTests
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFF;

    /// <summary>A minimal 3-sector CFBF whose directory chain points at itself
    /// (fat[0] = 0) — a cycle a bit-flip or a crafted file can produce.</summary>
    private static byte[] BuildCyclicCompoundFile()
    {
        var data = new byte[512 * 3];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(data, 0);
        U16(data, 30, 9);              // 512-byte sectors
        U16(data, 32, 6);              // 64-byte mini sectors
        U32(data, 48, 0);              // directory starts at sector 0
        U32(data, 56, 4096);           // mini-stream cutoff
        U32(data, 60, EndOfChain);     // no mini-FAT
        U32(data, 68, EndOfChain);     // no DIFAT chain
        U32(data, 76, 1);              // DIFAT[0]: the FAT lives in sector 1
        for (int i = 1; i < 109; i++) U32(data, 76 + i * 4, FreeSect);

        // Sector 1 = the FAT. fat[0] = 0 makes the directory chain cyclic.
        U32(data, 1024 + 0 * 4, 0);
        U32(data, 1024 + 1 * 4, EndOfChain);
        for (int i = 2; i < 128; i++) U32(data, 1024 + i * 4, FreeSect);
        return data;
    }

    [Fact]
    public void Cyclic_sector_chain_degrades_gracefully_instead_of_exhausting_memory()
    {
        // Pre-fix this loops until the backing MemoryStream blows past 2 GB.
        var cf = new CompoundFile(BuildCyclicCompoundFile());
        Assert.Empty(cf.Streams);
    }

    [Fact]
    public void Cyclic_msi_returns_null_metadata_rather_than_failing_the_package()
    {
        var path = Path.Combine(Path.GetTempPath(), "wraptune-cyclic-" + Guid.NewGuid().ToString("N") + ".msi");
        File.WriteAllBytes(path, BuildCyclicCompoundFile());
        try
        {
            Assert.Null(MsiPropertyReader.TryRead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void U16(byte[] d, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(off, 2), v);
    private static void U32(byte[] d, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(off, 4), v);
}
