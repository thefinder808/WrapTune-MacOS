using System.IO.Compression;
using System.Security.Cryptography;

namespace WrapTuneMacOS.Packaging;

/// <summary>Builds <c>.intunewin</c> packages.</summary>
public interface IIntuneWinPackager
{
    Task<PackageResult> PackageAsync(
        PackageRequest request, IProgress<string>? log = null, CancellationToken ct = default);
}

/// <summary>
/// In-house implementation of the <c>.intunewin</c> format (no official binary,
/// no third-party dependency). Pure BCL: <see cref="System.IO.Compression"/> +
/// <see cref="System.Security.Cryptography"/>.
///
/// Output is an OPC ZIP containing:
///   IntuneWinPackage/Contents/IntunePackage.intunewin   (encrypted payload)
///   IntuneWinPackage/Metadata/Detection.xml             (metadata + keys)
///
/// Encrypted payload layout: <c>HMAC-SHA256(32) || IV(16) || AES-256-CBC ciphertext</c>,
/// where the HMAC covers <c>IV || ciphertext</c> under a MacKey distinct from the
/// encryption key. See CLAUDE.md for the full spec.
/// </summary>
public sealed class IntuneWinWriter : IIntuneWinPackager
{
    /// <summary>Name of the encrypted content entry inside the package. Single
    /// source of truth is <see cref="DetectionXml.ContentFileName"/> — the zip
    /// entry name and the &lt;FileName&gt; element must never diverge.</summary>
    public const string ContentFileName = DetectionXml.ContentFileName;

    /// <summary>
    /// Reported in Detection.xml. Mirrors a recent official Content Prep Tool
    /// version for compatibility; bump when re-baselining against a newer
    /// official tool (and re-check the golden fixture).
    /// </summary>
    public const string ToolVersion = "1.8.6.0";

    private const string ContentsEntry = "IntuneWinPackage/Contents/" + ContentFileName;
    private const string MetadataEntry = "IntuneWinPackage/Metadata/Detection.xml";

    private const int KeyBytes = 32;   // AES-256 key and MAC key
    private const int IvBytes = 16;    // AES block size
    private const int MacBytes = 32;   // HMAC-SHA256 output

    public Task<PackageResult> PackageAsync(
        PackageRequest request, IProgress<string>? log = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try { return PackageCore(request, log, ct); }
            catch (OperationCanceledException) { return PackageResult.Fail("Packaging was cancelled."); }
            catch (Exception ex) { return PackageResult.Fail(ex.Message); }
        }, ct);
    }

    private static PackageResult PackageCore(PackageRequest request, IProgress<string>? log, CancellationToken ct)
    {
        // ── Validate ────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(request.SourceFolder))
            return PackageResult.Fail("Source folder is not specified.");
        if (string.IsNullOrWhiteSpace(request.SetupFile))
            return PackageResult.Fail("Setup file is not specified.");

        // TrimEndingDirectorySeparator so a user-supplied trailing slash on the
        // source folder doesn't break the containment check below.
        var sourceFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceFolder));
        var setupFile = Path.GetFullPath(request.SetupFile);

        if (!Directory.Exists(sourceFolder))
            return PackageResult.Fail("Source folder does not exist.");
        if (!File.Exists(setupFile))
            return PackageResult.Fail("Setup file does not exist.");

        // The setup file must live inside the source folder (the format records a
        // relative path). GetRelativePath normalises trailing slashes; a result
        // that escapes upward ("..") or stays absolute means it's outside.
        var relative = Path.GetRelativePath(sourceFolder, setupFile);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return PackageResult.Fail("Setup file must be inside the source folder.");

        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            return PackageResult.Fail("Output folder is not specified.");

        Directory.CreateDirectory(request.OutputFolder);

        // The target is a Windows endpoint, so the recorded setup path uses
        // backslashes to match the official tool's output.
        var setupRelative = relative
            .Replace(Path.DirectorySeparatorChar, '\\')
            .Replace('/', '\\');
        var setupName = Path.GetFileName(setupFile);

        var outputPath = Path.Combine(
            Path.GetFullPath(request.OutputFolder),
            Path.GetFileNameWithoutExtension(setupFile) + ".intunewin");

        if (File.Exists(outputPath) && !request.Overwrite)
            return PackageResult.Fail($"Output already exists: {outputPath}. Enable overwrite to replace it.");

        var work = Path.Combine(Path.GetTempPath(), "wraptune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var innerZip = Path.Combine(work, "inner.zip");
        var encrypted = Path.Combine(work, "encrypted.bin");

        try
        {
            ct.ThrowIfCancellationRequested();
            WarnIfTempSpaceLow(sourceFolder, work, log);

            // ── 1. Zip the source folder (the unencrypted payload) ──────────
            // NOTE: ZipFile.CreateFromDirectory has no cancellation hook, so a
            // cancel during this stage takes effect at the next stage boundary.
            log?.Report("Zipping payload…");
            ZipFile.CreateFromDirectory(sourceFolder, innerZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            var unencryptedSize = new FileInfo(innerZip).Length;

            // ── 2. SHA-256 of the payload, before encryption ───────────────
            ct.ThrowIfCancellationRequested();
            byte[] fileDigest;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(innerZip))
                fileDigest = sha.ComputeHash(fs);

            // ── 3. Keys / IV from a CSPRNG ──────────────────────────────────
            var encryptionKey = RandomNumberGenerator.GetBytes(KeyBytes);
            var macKey = RandomNumberGenerator.GetBytes(KeyBytes);
            var iv = RandomNumberGenerator.GetBytes(IvBytes);

            // ── 4-6. Encrypt + HMAC, streamed, into [Mac||IV||ciphertext] ──
            log?.Report("Encrypting (AES-256-CBC) and computing HMAC-SHA256…");
            ct.ThrowIfCancellationRequested();
            EncryptToFile(innerZip, encrypted, encryptionKey, macKey, iv, log, ct);

            // ── 7. MSI metadata (only for .msi setup files) ────────────────
            MsiInfo? msiInfo = null;
            if (Path.GetExtension(setupFile).Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                log?.Report("Reading MSI metadata…");
                msiInfo = Msi.MsiPropertyReader.TryRead(setupFile);
                log?.Report(msiInfo is null
                    ? "WARNING  Could not read MSI metadata; packaging without MsiInfo."
                    : $"MSI: {msiInfo.MsiProductCode} {msiInfo.MsiProductVersion}");
            }

            // ── 8. Detection.xml ────────────────────────────────────────────
            log?.Report("Writing Detection.xml…");
            var detection = new DetectionXml
            {
                Name = setupName,
                UnencryptedContentSize = unencryptedSize,
                SetupFile = setupRelative,
                EncryptionInfo = new EncryptionInfo
                {
                    EncryptionKey = Convert.ToBase64String(encryptionKey),
                    MacKey = Convert.ToBase64String(macKey),
                    InitializationVector = Convert.ToBase64String(iv),
                    Mac = Convert.ToBase64String(ReadMac(encrypted)),
                    FileDigest = Convert.ToBase64String(fileDigest),
                },
                MsiInfo = msiInfo,
            };
            var detectionBytes = detection.ToBytes();

            // ── 9. Assemble the outer OPC zip ──────────────────────────────
            log?.Report("Assembling package…");
            ct.ThrowIfCancellationRequested();
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            using (var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(encrypted, ContentsEntry, CompressionLevel.NoCompression);
                var meta = zip.CreateEntry(MetadataEntry, CompressionLevel.Optimal);
                using var s = meta.Open();
                s.Write(detectionBytes, 0, detectionBytes.Length);
            }

            log?.Report($"Created {outputPath}");
            return PackageResult.Ok(outputPath);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// Encrypt <paramref name="sourcePath"/> into <paramref name="destPath"/> as
    /// <c>Mac(32) || IV(16) || ciphertext</c>. The HMAC (over IV+ciphertext) is
    /// computed in one streaming pass and back-filled into the leading 32 bytes.
    /// Copies chunk-by-chunk so cancellation takes effect mid-file and progress
    /// can be reported for multi-GB payloads.
    /// </summary>
    private static void EncryptToFile(string sourcePath, string destPath, byte[] encryptionKey, byte[] macKey, byte[] iv,
        IProgress<string>? log, CancellationToken ct)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        hmac.AppendData(iv);

        using var dest = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite);
        dest.Write(new byte[MacBytes]);  // placeholder for the MAC, filled in last
        dest.Write(iv);

        using (var aes = Aes.Create())
        {
            aes.KeySize = 256;
            aes.Key = encryptionKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            using var tee = new HmacTeeStream(dest, hmac);  // every ciphertext byte → file + HMAC
            using (var crypto = new CryptoStream(tee, encryptor, CryptoStreamMode.Write, leaveOpen: true))
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read))
            {
                var buffer = new byte[1 << 20];
                long total = source.Length, done = 0;
                int lastReported = 0, n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    crypto.Write(buffer, 0, n);
                    done += n;
                    var pct = total > 0 ? (int)(done * 100 / total) : 100;
                    if (pct >= lastReported + 10 && pct < 100)
                    {
                        log?.Report($"Encrypting… {pct}%");
                        lastReported = pct;
                    }
                }
                crypto.FlushFinalBlock();
            }
        }

        var mac = hmac.GetHashAndReset();
        dest.Seek(0, SeekOrigin.Begin);
        dest.Write(mac);
    }

    /// <summary>Scratch files (inner zip + encrypted blob) live on the temp
    /// volume and together need roughly twice the payload size there — which can
    /// be a different, smaller volume than the output folder. Warn up front so a
    /// mid-run disk-full IOException isn't the first sign.</summary>
    private static void WarnIfTempSpaceLow(string sourceFolder, string workDir, IProgress<string>? log)
    {
        try
        {
            var needed = DirectorySizeBytes(sourceFolder) * 2;
            var free = new DriveInfo(workDir).AvailableFreeSpace;
            if (free < needed)
                log?.Report($"WARNING  The temp volume has {free / (1024 * 1024)} MB free but packaging " +
                            $"may need up to ~{needed / (1024 * 1024)} MB of scratch space.");
        }
        catch
        {
            // Best-effort advisory only — never block packaging on it.
        }
    }

    internal static long DirectorySizeBytes(string root)
    {
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch { /* unreadable entry — skip */ }
        }
        return total;
    }

    private static byte[] ReadMac(string encryptedPath)
    {
        using var fs = File.OpenRead(encryptedPath);
        var mac = new byte[MacBytes];
        fs.ReadExactly(mac);
        return mac;
    }

    /// <summary>
    /// Write-only pass-through stream that forwards bytes to an inner stream and
    /// also feeds them into an <see cref="IncrementalHash"/>. Does not own the
    /// inner stream.
    /// </summary>
    private sealed class HmacTeeStream(Stream inner, IncrementalHash hmac) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            hmac.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hmac.AppendData(buffer);
            inner.Write(buffer);
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
