using System.Buffers.Binary;
using System.Text;

namespace WrapTuneMacOS.Packaging.Msi;

/// <summary>
/// Minimal reader for an OLE Property Set stream (MS-OLEPS) — used to pull the
/// PackageCode (Revision Number, PID 9) out of an MSI's SummaryInformation.
/// </summary>
internal static class OlePropertySet
{
    private const uint VtLpstr = 0x1E;
    private const uint PidCodepage = 1;

    /// <summary>Read the VT_LPSTR value of a given property id, or null.</summary>
    public static string? ReadString(byte[] s, int propertyId)
    {
        if (s.Length < 48) return null;

        // Header → first section offset lives at byte 44 (after byteorder/version/
        // sysid/clsid/numSections/FMTID).
        int section = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(44, 4));
        if (section < 0 || section + 8 > s.Length) return null;

        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(section + 4, 4));
        int codepage = 1252;
        int targetAbs = -1;

        for (int i = 0; i < count; i++)
        {
            int entry = section + 8 + i * 8;
            if (entry + 8 > s.Length) break;
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(entry, 4));
            uint rel = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(entry + 4, 4));
            int abs = section + (int)rel;

            if (pid == PidCodepage && abs + 6 <= s.Length)
            {
                // VT_I2 codepage; stored signed but we only need it for ASCII.
                short cp = BinaryPrimitives.ReadInt16LittleEndian(s.AsSpan(abs + 4, 2));
                if (cp != 0 && cp != 1200) codepage = cp;
            }
            if (pid == (uint)propertyId) targetAbs = abs;
        }

        if (targetAbs < 0 || targetAbs + 8 > s.Length) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(targetAbs, 4)) != VtLpstr) return null;

        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(targetAbs + 4, 4));
        if (len <= 0 || targetAbs + 8 + len > s.Length) return null;

        return EncodingFor(codepage).GetString(s, targetAbs + 8, len).TrimEnd('\0');
    }

    private static Encoding EncodingFor(int cp)
    {
        if (cp is 0 or 1252) return Encoding.Latin1;
        if (cp == 65001) return Encoding.UTF8;
        try { return Encoding.GetEncoding(cp); } catch { return Encoding.Latin1; }
    }
}
