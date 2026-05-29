using System.IO.Compression;
using System.Security.Cryptography;

namespace WrapTuneMacOS.Packaging;

/// <summary>The decrypted, verified contents of a <c>.intunewin</c> package.</summary>
/// <param name="Detection">Parsed Detection.xml.</param>
/// <param name="DecryptedZip">The recovered inner ZIP (the original payload).</param>
/// <param name="MacValid">HMAC over IV+ciphertext matched the recorded MAC.</param>
/// <param name="DigestValid">SHA-256 of the decrypted payload matched FileDigest.</param>
/// <param name="SizeValid">Decrypted length matched UnencryptedContentSize.</param>
public sealed record IntuneWinContents(
    DetectionXml Detection, byte[] DecryptedZip, bool MacValid, bool DigestValid, bool SizeValid)
{
    /// <summary>True when MAC, digest, and size all check out.</summary>
    public bool IsValid => MacValid && DigestValid && SizeValid;
}

/// <summary>
/// Reads and decrypts a <c>.intunewin</c> package — used for the round-trip
/// self-test and to decrypt known-good packages from the official tool. Works on
/// our own output and on official output identically (same documented format).
/// </summary>
public static class IntuneWinReader
{
    private const string ContentsEntry = "IntuneWinPackage/Contents/" + IntuneWinWriter.ContentFileName;
    private const string MetadataEntry = "IntuneWinPackage/Metadata/Detection.xml";
    private const int IvBytes = 16;
    private const int MacBytes = 32;

    public static IntuneWinContents Read(string intuneWinPath)
    {
        using var fs = File.OpenRead(intuneWinPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var metaEntry = zip.GetEntry(MetadataEntry)
            ?? throw new FormatException($"Package is missing {MetadataEntry}.");
        var contentEntry = zip.GetEntry(ContentsEntry)
            ?? throw new FormatException($"Package is missing {ContentsEntry}.");

        byte[] metaBytes = ReadEntry(metaEntry);
        byte[] blob = ReadEntry(contentEntry);

        var detection = DetectionXml.Parse(metaBytes);
        var enc = detection.EncryptionInfo;

        var encryptionKey = Convert.FromBase64String(enc.EncryptionKey);
        var macKey = Convert.FromBase64String(enc.MacKey);
        var recordedMac = Convert.FromBase64String(enc.Mac);

        if (blob.Length < MacBytes + IvBytes)
            throw new FormatException("Encrypted content is too short.");

        // Layout: Mac(32) || IV(16) || ciphertext. HMAC covers IV+ciphertext.
        var storedMac = blob.AsSpan(0, MacBytes).ToArray();
        var hashedRegion = blob.AsSpan(MacBytes).ToArray();   // IV || ciphertext
        var iv = blob.AsSpan(MacBytes, IvBytes).ToArray();
        var ciphertext = blob.AsSpan(MacBytes + IvBytes).ToArray();

        using var hmac = new HMACSHA256(macKey);
        var computedMac = hmac.ComputeHash(hashedRegion);
        bool macValid = CryptographicOperations.FixedTimeEquals(computedMac, storedMac)
                        && CryptographicOperations.FixedTimeEquals(storedMac, recordedMac);

        // Verify-then-decrypt: never process unauthenticated ciphertext. A
        // tampered payload fails here and we stop, rather than risk a padding-
        // oracle decrypt of attacker-controlled bytes.
        if (!macValid)
            return new IntuneWinContents(detection, [], MacValid: false, DigestValid: false, SizeValid: false);

        byte[] decrypted;
        try
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = encryptionKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            decrypted = aes.DecryptCbc(ciphertext, iv);
        }
        catch (CryptographicException)
        {
            // Authenticated but undecryptable ⇒ key mismatch / corruption.
            return new IntuneWinContents(detection, [], MacValid: true, DigestValid: false, SizeValid: false);
        }

        var digest = SHA256.HashData(decrypted);
        bool digestValid = CryptographicOperations.FixedTimeEquals(
            digest, Convert.FromBase64String(enc.FileDigest));
        bool sizeValid = decrypted.LongLength == detection.UnencryptedContentSize;

        return new IntuneWinContents(detection, decrypted, macValid, digestValid, sizeValid);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
