using System.Text;

namespace WrapTuneMacOS.Packaging.Msi;

/// <summary>
/// MSI mangles table/stream names into a private Unicode range. This decodes a
/// raw stream name back to its logical name (e.g. the encoded "Property" stream).
/// Mirrors the well-known algorithm (Wine/libmsi <c>decode_streamname</c>).
/// </summary>
internal static class MsiStreamName
{
    // 64-char alphabet: 0-9, A-Z, a-z, '.', '_'.
    private const string Mime =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";

    private static char Decode64(int x) => Mime[x & 0x3f];

    public static string Decode(string raw)
    {
        var sb = new StringBuilder(raw.Length * 2);
        foreach (char c in raw)
        {
            if (c >= 0x3800 && c < 0x4840)
            {
                if (c >= 0x4800)                 // one encoded character
                {
                    sb.Append(Decode64(c - 0x4800));
                }
                else                             // two encoded characters
                {
                    int ch = c - 0x3800;
                    sb.Append(Decode64(ch & 0x3f));
                    sb.Append(Decode64(ch >> 6));
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
