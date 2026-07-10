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

/// <summary>Validation outcome of <see cref="IntuneWinReader.ReadToFile"/> — the
/// payload itself lands on disk, so no plaintext is held in memory.</summary>
public sealed record IntuneWinFileContents(
    DetectionXml Detection, bool MacValid, bool DigestValid, bool SizeValid)
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

    /// <summary>
    /// Decrypt a package's payload to <paramref name="destZipPath"/>, streaming
    /// throughout — memory stays O(buffer) even for multi-GB packages. The content
    /// entry is read twice (HMAC pass, then decrypt pass) because verify-then-
    /// decrypt requires authenticating every ciphertext byte before any of it is
    /// fed to AES; zip entry streams can't seek, so the entry is simply reopened.
    /// On MAC failure or undecryptable content no plaintext is left on disk.
    /// </summary>
    public static IntuneWinFileContents ReadToFile(string intuneWinPath, string destZipPath)
    {
        using var fs = File.OpenRead(intuneWinPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var metaEntry = zip.GetEntry(MetadataEntry)
            ?? throw new FormatException($"Package is missing {MetadataEntry}.");
        var contentEntry = zip.GetEntry(ContentsEntry)
            ?? throw new FormatException($"Package is missing {ContentsEntry}.");

        var detection = DetectionXml.Parse(ReadEntry(metaEntry));
        var enc = detection.EncryptionInfo;

        var encryptionKey = Convert.FromBase64String(enc.EncryptionKey);
        var macKey = Convert.FromBase64String(enc.MacKey);
        var recordedMac = Convert.FromBase64String(enc.Mac);

        // ── Pass 1: authenticate. HMAC covers IV || ciphertext. ──
        var storedMac = new byte[MacBytes];
        var iv = new byte[IvBytes];
        byte[] computedMac;
        using (var es = contentEntry.Open())
        {
            ReadHeader(es, storedMac, iv);
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
            hmac.AppendData(iv);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = es.Read(buffer, 0, buffer.Length)) > 0)
                hmac.AppendData(buffer.AsSpan(0, n));
            computedMac = hmac.GetHashAndReset();
        }

        // Verify-then-decrypt: never process unauthenticated ciphertext. A
        // tampered payload fails here and we stop, rather than risk a padding-
        // oracle decrypt of attacker-controlled bytes.
        bool macValid = CryptographicOperations.FixedTimeEquals(computedMac, storedMac)
                        && CryptographicOperations.FixedTimeEquals(storedMac, recordedMac);
        if (!macValid)
            return new IntuneWinFileContents(detection, MacValid: false, DigestValid: false, SizeValid: false);

        // ── Pass 2: decrypt to disk, hashing the plaintext as it lands. ──
        long plainLength = 0;
        byte[] digest;
        try
        {
            using var es = contentEntry.Open();
            ReadHeader(es, storedMac, iv);   // skip the already-verified header

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = encryptionKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor(encryptionKey, iv);
            using var crypto = new CryptoStream(es, decryptor, CryptoStreamMode.Read);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var dst = File.Create(destZipPath);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = crypto.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer.AsSpan(0, n));
                dst.Write(buffer, 0, n);
                plainLength += n;
            }
            digest = sha.GetHashAndReset();
        }
        catch (CryptographicException)
        {
            // Authenticated but undecryptable ⇒ key mismatch / corruption.
            // Don't leave a partial plaintext behind.
            try { File.Delete(destZipPath); } catch { /* best-effort */ }
            return new IntuneWinFileContents(detection, MacValid: true, DigestValid: false, SizeValid: false);
        }

        bool digestValid = CryptographicOperations.FixedTimeEquals(
            digest, Convert.FromBase64String(enc.FileDigest));
        bool sizeValid = plainLength == detection.UnencryptedContentSize;

        return new IntuneWinFileContents(detection, macValid, digestValid, sizeValid);
    }

    /// <summary>In-memory convenience over <see cref="ReadToFile"/> — fine for
    /// tests and small packages; prefer ReadToFile for anything large.</summary>
    public static IntuneWinContents Read(string intuneWinPath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wraptune-read-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var r = ReadToFile(intuneWinPath, tmp);
            var payload = File.Exists(tmp) ? File.ReadAllBytes(tmp) : [];
            return new IntuneWinContents(r.Detection, payload, r.MacValid, r.DigestValid, r.SizeValid);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    private static void ReadHeader(Stream s, byte[] mac, byte[] iv)
    {
        try
        {
            s.ReadExactly(mac);
            s.ReadExactly(iv);
        }
        catch (EndOfStreamException)
        {
            throw new FormatException("Encrypted content is too short.");
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
