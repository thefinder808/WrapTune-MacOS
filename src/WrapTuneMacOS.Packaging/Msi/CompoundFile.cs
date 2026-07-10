using System.Buffers.Binary;
using System.Text;

namespace WrapTuneMacOS.Packaging.Msi;

/// <summary>
/// Minimal read-only OLE2 / Compound File Binary Format (MS-CFB) reader —
/// enough to pull named streams out of an MSI. Pure managed, no Windows APIs.
/// </summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFF;
    private const int GuardLimit = 20_000_000;   // chain-length backstop

    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public readonly record struct DirEntry(string Name, byte Type, uint StartSector, long Size);

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly int _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;

    /// <summary>All stream (type 2) directory entries.</summary>
    public IReadOnlyList<DirEntry> Streams { get; }

    public CompoundFile(byte[] data)
    {
        _data = data;
        if (data.Length < 512 || !data.AsSpan(0, 8).SequenceEqual(Signature))
            throw new FormatException("Not a compound file (bad CFBF signature).");

        int sectorShift = U16(30);
        int miniSectorShift = U16(32);
        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniSectorShift;
        _miniCutoff = (int)U32(56);

        uint firstDirSector = U32(48);
        uint firstMiniFatSector = U32(60);
        uint firstDifatSector = U32(68);

        // ── DIFAT → list of FAT sector locations ───────────────────────────
        var fatSectors = new List<uint>();
        for (int i = 0; i < 109; i++)
        {
            uint s = U32(76 + i * 4);
            if (s != FreeSect && s != EndOfChain) fatSectors.Add(s);
        }
        int entriesPerSector = _sectorSize / 4;
        uint dif = firstDifatSector;
        int guard = 0;
        // A corrupt/crafted file can make any chain loop back on itself; the
        // visited sets bail on the first revisit so a cycle can't grow memory.
        var seenDifat = new HashSet<uint>();
        while (dif != EndOfChain && dif != FreeSect && guard++ < GuardLimit && seenDifat.Add(dif))
        {
            long off = SectorOffset(dif);
            for (int i = 0; i < entriesPerSector - 1; i++)
            {
                uint s = U32At(off + i * 4);
                if (s != FreeSect && s != EndOfChain) fatSectors.Add(s);
            }
            dif = U32At(off + (entriesPerSector - 1) * 4);
        }

        // ── FAT ─────────────────────────────────────────────────────────────
        _fat = new uint[(long)fatSectors.Count * entriesPerSector];
        int idx = 0;
        foreach (var fs in fatSectors)
        {
            long off = SectorOffset(fs);
            for (int i = 0; i < entriesPerSector; i++)
                _fat[idx++] = U32At(off + i * 4);
        }

        // ── Mini-FAT and directory ───────────────────────────────────────--
        _miniFat = ToUInts(ReadSectorChain(firstMiniFatSector, -1));
        var dirBytes = ReadSectorChain(firstDirSector, -1);

        var entries = new List<DirEntry>();
        DirEntry? root = null;
        for (int off = 0; off + 128 <= dirBytes.Length; off += 128)
        {
            byte type = dirBytes[off + 66];      // 1 storage, 2 stream, 5 root
            if (type is not (1 or 2 or 5)) continue;

            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(off + 64, 2));
            nameLen = nameLen >= 2 ? nameLen - 2 : 0;   // bytes incl. UTF-16 null terminator
            string name = Encoding.Unicode.GetString(dirBytes, off, nameLen);
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(off + 116, 4));
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(dirBytes.AsSpan(off + 120, 8));
            if (_sectorSize == 512) size &= 0xFFFFFFFF;   // v3: only low 32 bits valid

            var entry = new DirEntry(name, type, start, (long)size);
            if (type == 5) root = entry;
            else if (type == 2) entries.Add(entry);
        }
        Streams = entries;

        // Root entry's stream is the mini-stream container.
        _miniStream = root is { } r ? ReadSectorChain(r.StartSector, r.Size) : [];
    }

    public byte[] Read(DirEntry e) =>
        e.Size >= _miniCutoff ? ReadSectorChain(e.StartSector, e.Size) : ReadMiniChain(e.StartSector, e.Size);

    // ── helpers ───────────────────────────────────────────────────────────--

    private long SectorOffset(uint sector) => (long)(sector + 1) * _sectorSize;

    private byte[] ReadSectorChain(uint start, long size)
    {
        using var ms = new MemoryStream();
        uint s = start;
        int guard = 0;
        var seen = new HashSet<uint>();
        while (s != EndOfChain && s != FreeSect && s < _fat.Length && guard++ < GuardLimit && seen.Add(s))
        {
            long off = SectorOffset(s);
            if (off < 0 || off + _sectorSize > _data.Length) break;
            ms.Write(_data, (int)off, _sectorSize);
            s = _fat[s];
        }
        return Trim(ms.ToArray(), size);
    }

    private byte[] ReadMiniChain(uint start, long size)
    {
        using var ms = new MemoryStream();
        uint s = start;
        int guard = 0;
        var seen = new HashSet<uint>();
        while (s != EndOfChain && s != FreeSect && s < _miniFat.Length && guard++ < GuardLimit && seen.Add(s))
        {
            long off = (long)s * _miniSectorSize;
            if (off < 0 || off + _miniSectorSize > _miniStream.Length) break;
            ms.Write(_miniStream, (int)off, _miniSectorSize);
            s = _miniFat[s];
        }
        return Trim(ms.ToArray(), size);
    }

    private static byte[] Trim(byte[] all, long size)
    {
        if (size >= 0 && size < all.Length) Array.Resize(ref all, (int)size);
        return all;
    }

    private static uint[] ToUInts(byte[] b)
    {
        var u = new uint[b.Length / 4];
        for (int i = 0; i < u.Length; i++)
            u[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4, 4));
        return u;
    }

    private int U16(int off) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off, 2));
    private uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off, 4));
    private uint U32At(long off) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)off, 4));
}
