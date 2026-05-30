using System.Text;

namespace WrapTuneMacOS.Signing;

/// <summary>
/// Authenticode-signs the Win32 payload <b>before</b> it is wrapped into a
/// <c>.intunewin</c>, by shelling out to <c>osslsigncode</c>. This lives entirely
/// outside <c>WrapTuneMacOS.Packaging</c>: the <c>.intunewin</c> engine stays pure
/// managed code with zero external-process calls, so its supply-chain
/// auditability is untouched. Signing only mutates the <i>payload</i> files.
///
/// Files are signed <b>in place</b> (osslsigncode can't write its own input, so we
/// sign to a sibling temp and atomically replace). Files that already carry a
/// signature are skipped, so vendor-signed installers are never clobbered.
/// </summary>
public sealed class PayloadSigner
{
    private readonly string _osslsigncode;

    private PayloadSigner(string osslsigncode) => _osslsigncode = osslsigncode;

    /// <summary>
    /// Create a signer, resolving the osslsigncode binary. Returns null and sets
    /// <paramref name="error"/> (the install hint) when the tool can't be found.
    /// </summary>
    public static PayloadSigner? TryCreate(SigningOptions options, out string? error)
    {
        var exe = SignerLocator.Locate(options.OsslsigncodePath);
        if (exe is null) { error = SignerLocator.InstallHint; return null; }
        error = null;
        return new PayloadSigner(exe);
    }

    /// <summary>
    /// Sign the payload in place. Always signs the setup file; when
    /// <see cref="SigningOptions.SignAllSignableFiles"/> is set, also signs every
    /// other signable file under <paramref name="sourceFolder"/>. Already-signed
    /// files are skipped. Progress is streamed via <paramref name="log"/>.
    /// </summary>
    public async Task<SignResult> SignAsync(
        string sourceFolder, string setupFile, SigningOptions options,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var setupSignable = SignableExtensions.IsSignable(setupFile);
        if (!options.SignAllSignableFiles && !setupSignable)
            return SignResult.Fail(
                $"'{Path.GetFileName(setupFile)}' is a script type Authenticode can't sign (.cmd/.bat). " +
                "Turn on \"sign all signable files\" or choose a signable setup file.");

        var targets = CollectTargets(sourceFolder, setupFile, options);
        if (targets.Count == 0)
            return SignResult.Fail("No Authenticode-signable files were found to sign.");

        if (options.SignAllSignableFiles && !setupSignable)
            log?.Report($"Note: setup file '{Path.GetFileName(setupFile)}' isn't a signable type — signing the other files.");

        string? passFile = null;
        try
        {
            passFile = WriteSecretFile(options.Secret);

            foreach (var file in targets)
            {
                ct.ThrowIfCancellationRequested();

                if (await ExistingSignatureAsync(file, ct) is { } subject)
                {
                    log?.Report($"Skipping {Path.GetFileName(file)} — already signed ({subject}).");
                    continue;
                }

                log?.Report($"Signing {Path.GetFileName(file)}…");
                if (await SignFileAsync(file, options, passFile, ct) is { } err)
                    return SignResult.Fail($"Failed to sign {Path.GetFileName(file)}: {err}");
                log?.Report($"Signed {Path.GetFileName(file)}.");
            }

            return SignResult.Ok();
        }
        finally
        {
            if (passFile is not null) TryDelete(passFile);
        }
    }

    private static List<string> CollectTargets(string sourceFolder, string setupFile, SigningOptions options)
    {
        var setupFull = Path.GetFullPath(setupFile);
        if (!options.SignAllSignableFiles)
            return SignableExtensions.IsSignable(setupFull) ? [setupFull] : [];

        var targets = new List<string>();
        foreach (var f in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
            if (SignableExtensions.IsSignable(f))
                targets.Add(Path.GetFullPath(f));
        return targets;
    }

    /// <summary>
    /// Sign one file in place: <c>osslsigncode sign … -in file -out file.signtmp</c>,
    /// then atomically replace the original. Returns null on success, else an error.
    /// </summary>
    private async Task<string?> SignFileAsync(string file, SigningOptions options, string? passFile, CancellationToken ct)
    {
        var temp = file + ".signtmp";
        try
        {
            var args = BuildSignArgs(file, temp, options, passFile);
            var (exit, stdout, stderr) = await ProcessRunner.RunAsync(_osslsigncode, args, ct);
            if (exit != 0)
                return Summarize(stderr, stdout);
            if (!File.Exists(temp))
                return "osslsigncode reported success but wrote no output file.";

            File.Move(temp, file, overwrite: true);   // same volume → atomic rename
            return null;
        }
        finally
        {
            TryDelete(temp);   // no-op after a successful move
        }
    }

    /// <summary>
    /// Detect an existing signature so we never clobber one (e.g. a vendor-signed
    /// MSI). Returns a short signer description when a signature is present, or
    /// null when the file is unsigned. osslsigncode prints "No signature found"
    /// for unsigned files; any other result is treated as "a signature exists" so
    /// we err toward skipping rather than overwriting.
    /// </summary>
    private async Task<string?> ExistingSignatureAsync(string file, CancellationToken ct)
    {
        try
        {
            var (exit, stdout, stderr) = await ProcessRunner.RunAsync(_osslsigncode, ["verify", "-in", file], ct);
            var output = stdout + "\n" + stderr;

            if (output.Contains("No signature found", StringComparison.OrdinalIgnoreCase))
                return null;

            var present = exit == 0
                || output.Contains("Signer", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Subject", StringComparison.OrdinalIgnoreCase);
            if (!present) return null;

            return ExtractSubject(output) ?? "existing signature";
        }
        catch
        {
            // Couldn't determine — fall through to signing; the sign step surfaces real errors.
            return null;
        }
    }

    private static string? ExtractSubject(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            var idx = t.IndexOf("Subject:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var value = t[(idx + "Subject:".Length)..].Trim();
                if (value.Length > 0) return value;
            }
        }
        return null;
    }

    /// <summary>
    /// Build the osslsigncode <c>sign</c> argument list. Internal + static so it can
    /// be unit-tested offline (no binary required).
    /// </summary>
    internal static List<string> BuildSignArgs(string inFile, string outFile, SigningOptions o, string? passFile)
    {
        var args = new List<string> { "sign" };

        if (o.CertMode == CertMode.Pkcs11)
        {
            args.Add("-pkcs11module");
            args.Add(o.Pkcs11ModulePath ?? "");
            if (!string.IsNullOrWhiteSpace(o.Pkcs11CertUri))
            {
                args.Add("-pkcs11cert");
                args.Add(o.Pkcs11CertUri);
            }
            args.Add("-key");
            args.Add(o.KeyUri ?? "");
        }
        else
        {
            args.Add("-pkcs12");
            args.Add(o.PfxPath ?? "");
        }

        if (passFile is not null)
        {
            args.Add("-readpass");
            args.Add(passFile);
        }

        args.Add("-h");
        args.Add("sha256");

        if (!string.IsNullOrWhiteSpace(o.Description))
        {
            args.Add("-n");
            args.Add(o.Description);
        }
        if (!string.IsNullOrWhiteSpace(o.Url))
        {
            args.Add("-i");
            args.Add(o.Url);
        }
        if (!string.IsNullOrWhiteSpace(o.TimestampUrl))
        {
            args.Add("-ts");   // RFC3161 timestamping
            args.Add(o.TimestampUrl);
        }

        args.Add("-in");
        args.Add(inFile);
        args.Add("-out");
        args.Add(outFile);
        return args;
    }

    /// <summary>
    /// Write the secret to a <c>0600</c> temp file for <c>-readpass</c>. The file is
    /// created with owner-only permissions <i>before</i> the secret is written, and
    /// the caller deletes it immediately after signing. Returns null when there is
    /// no secret (the <c>-readpass</c> flag is then omitted entirely).
    /// </summary>
    private static string? WriteSecretFile(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;

        var path = Path.Combine(Path.GetTempPath(), "wt-sign-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // Lock down permissions on the empty file first, then write the secret.
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.WriteAllText(path, secret, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    private static string Summarize(string stderr, string stdout)
    {
        var text = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return string.IsNullOrEmpty(line) ? "osslsigncode failed (no diagnostic output)." : line;
    }
}
