using System.Buffers.Binary;
using System.Text;

namespace WrapTuneMacOS.Packaging.Msi;

/// <summary>
/// Reads the parts of an MSI we need: the string pool, the Property table, and
/// the PackageCode from SummaryInformation. Built on <see cref="CompoundFile"/>.
/// </summary>
internal sealed class MsiDatabase
{
    private readonly CompoundFile _cf;
    private readonly Dictionary<string, CompoundFile.DirEntry> _byDecoded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompoundFile.DirEntry> _byRaw = new(StringComparer.Ordinal);

    public MsiDatabase(byte[] data)
    {
        _cf = new CompoundFile(data);
        foreach (var e in _cf.Streams)
        {
            _byRaw[e.Name] = e;

            // Decoded table names carry a leading U+4840 "table" marker;
            // strip it so lookups use the bare name (e.g. "Property").
            const char tableMarker = (char)0x4840;
            var decoded = MsiStreamName.Decode(e.Name);
            if (decoded.Length > 0 && decoded[0] == tableMarker)
                decoded = decoded[1..];
            _byDecoded[decoded] = e;
        }
    }

    private byte[]? Decoded(string name) => _byDecoded.TryGetValue(name, out var e) ? _cf.Read(e) : null;

    // ── String pool ─────────────────────────────────────────────────────--

    private sealed record StringTable(IReadOnlyList<string> Strings, int BytesPerRef)
    {
        public string this[int index] => index > 0 && index < Strings.Count ? Strings[index] : "";
    }

    private StringTable LoadStrings()
    {
        var pool = Decoded("_StringPool") ?? throw new FormatException("MSI has no _StringPool.");
        var data = Decoded("_StringData") ?? [];

        int entries = pool.Length / 4;
        var strings = new List<string> { "" };   // index 0 = the null string

        // Entry 0 is the header: combined uint = codepage, top bit = 3-byte refs.
        uint header = entries > 0 ? BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan(0, 4)) : 0;
        int codepage = (int)(header & 0x7FFFFFFF);
        int bytesPerRef = (header & 0x80000000) != 0 ? 3 : 2;
        var enc = EncodingFor(codepage);

        int offset = 0;
        for (int i = 1; i < entries; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4, 2));
            int refs = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4 + 2, 2));

            // len==0, refs!=0 ⇒ long string: refs is the high word of the length,
            // the next entry's size word is the low word.
            if (len == 0 && refs != 0 && i + 1 < entries)
            {
                int low = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan((i + 1) * 4, 2));
                int longLen = (refs << 16) | low;
                strings.Add(ReadStr(data, offset, longLen, enc));
                offset += longLen;
                i++;
                continue;
            }

            strings.Add(ReadStr(data, offset, len, enc));
            offset += len;
        }

        if (strings.Count > 0xFFFF) bytesPerRef = 3;
        return new StringTable(strings, bytesPerRef);
    }

    private static string ReadStr(byte[] data, int offset, int len, Encoding enc) =>
        offset >= 0 && len >= 0 && offset + len <= data.Length ? enc.GetString(data, offset, len) : "";

    // ── Property table ─────────────────────────────────────────────────--

    /// <summary>The MSI Property table as a name→value map (empty if absent).</summary>
    public Dictionary<string, string> ReadProperties()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var table = Decoded("Property");
        if (table is null) return result;

        var st = LoadStrings();
        int refSize = st.BytesPerRef;
        int rows = table.Length / (refSize * 2);
        if (rows == 0) return result;

        // Tables are column-major: all Property refs, then all Value refs.
        for (int r = 0; r < rows; r++)
        {
            string name = st[ReadRef(table, r * refSize, refSize)];
            string value = st[ReadRef(table, rows * refSize + r * refSize, refSize)];
            if (!string.IsNullOrEmpty(name))
                result[name] = value;
        }
        return result;
    }

    private static int ReadRef(byte[] b, int offset, int size)
    {
        int v = b[offset] | (b[offset + 1] << 8);
        if (size == 3) v |= b[offset + 2] << 16;
        return v;
    }

    // ── SummaryInformation: PackageCode (Revision Number, PID 9) ──────--

    public string? ReadPackageCode()
    {
        // The stream is named with a leading control char (U+0005). Match by
        // suffix so we don't depend on representing that char in source.
        byte[]? summary = null;
        foreach (var (name, e) in _byRaw)
        {
            if (name.EndsWith("SummaryInformation", StringComparison.Ordinal))
            {
                summary = _cf.Read(e);
                break;
            }
        }
        return summary is null ? null : OlePropertySet.ReadString(summary, propertyId: 9);
    }

    private static Encoding EncodingFor(int codepage)
    {
        // Property values we read (GUIDs, versions, publisher) are ASCII, so
        // Latin1 is a safe default; honour UTF-8 / real codepages when present.
        if (codepage is 0 or 1252) return Encoding.Latin1;
        if (codepage == 65001) return Encoding.UTF8;
        try { return Encoding.GetEncoding(codepage); } catch { return Encoding.Latin1; }
    }
}
