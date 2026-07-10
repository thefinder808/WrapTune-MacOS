using System.Reflection;
using WrapTuneMacOS.Packaging;
using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Cli;

/// <summary>A bad invocation — message goes to stderr, exit code 2.</summary>
internal sealed class CliUsageException(string message) : Exception(message);

/// <summary>
/// The wraptune command surface, on the same engine as the GUI (one core, two
/// frontends). Flags mirror the official IntuneWinAppUtil where they overlap
/// (-c/-s/-o, and -q = quiet + silently overwrite). Secrets are read from
/// environment variables only — never argv, which any local user can see in ps.
/// </summary>
internal static class Cli
{
    private const string SecretEnvVar = "WRAPTUNE_SIGN_SECRET";
    private const string AzureTokenEnvVar = "WRAPTUNE_AZURE_TOKEN";

    /// <summary>Flags that take no value.</summary>
    private static readonly HashSet<string> BoolFlags =
        ["--quiet", "--overwrite", "--sign-all"];

    /// <summary>Short → canonical flag names.</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["-c"] = "--source",
        ["-s"] = "--setup",
        ["-o"] = "--output",
        ["-q"] = "--quiet",
    };

    /// <summary>Every flag any command accepts — unknown flags are usage errors.</summary>
    private static readonly HashSet<string> KnownFlags =
    [
        "--source", "--setup", "--output", "--quiet", "--overwrite",
        "--pfx", "--pkcs11-module", "--pkcs11-thumbprint",
        "--azure-endpoint", "--azure-account", "--azure-profile",
        "--timestamp-url", "--description", "--sign-url", "--sign-all",
    ];

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        if (args.Length == 0) { stderr.Write(Usage); return 2; }
        if (args[0] is "--help" or "-h" or "help") { stdout.Write(Usage); return 0; }
        if (args[0] is "--version") { stdout.WriteLine(Version()); return 0; }

        // Muscle-memory nicety: `wraptune -c … -s … -o …` (official-tool style,
        // no subcommand) means pack.
        var command = args[0].StartsWith('-') ? "pack" : args[0];
        var rest = args[0].StartsWith('-') ? args : args[1..];

        try
        {
            return command switch
            {
                "pack" => await PackAsync(rest, stdout, stderr, ct),
                "inspect" => Inspect(rest, stdout),
                "extract" => Extract(rest, stdout, stderr),
                _ => throw new CliUsageException($"unknown command '{command}'"),
            };
        }
        catch (CliUsageException ex)
        {
            stderr.WriteLine("error: " + ex.Message);
            stderr.Write(Usage);
            return 2;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("Cancelled.");
            return 1;
        }
    }

    // ── pack ────────────────────────────────────────────────────────────────

    private static async Task<int> PackAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var opts = Parse(args, expectedPositionals: 0).Options;
        var source = Require(opts, "--source");
        var setup = Require(opts, "--setup");
        var output = Require(opts, "--output");
        bool quiet = opts.ContainsKey("--quiet");
        bool overwrite = quiet || opts.ContainsKey("--overwrite");   // official -q semantics
        var progress = quiet ? null : new LineWriter(stdout);

        var signing = BuildSigningOptions(opts, Environment.GetEnvironmentVariable);
        if (signing is not null)
        {
            var signer = PayloadSigner.TryCreate(signing, out var createError)
                ?? throw new CliUsageException(createError!);

            var signed = await signer.SignAsync(source, setup, signing, progress, ct);
            if (!signed.Success)
            {
                stderr.WriteLine("error: signing failed — " + signed.Error);
                return 1;
            }
        }

        var result = await new IntuneWinWriter().PackageAsync(
            new PackageRequest(source, setup, output, overwrite), progress, ct);
        if (!result.Success)
        {
            stderr.WriteLine("error: " + result.Error);
            return 1;
        }

        // Always print the artifact path, even in quiet mode — script-friendly.
        stdout.WriteLine(result.OutputPath);
        return 0;
    }

    // ── inspect ─────────────────────────────────────────────────────────────

    private const int MaxListedEntries = 200;

    private static int Inspect(string[] args, TextWriter stdout)
    {
        var path = SinglePackageArg(args);
        var i = TryInspect(path);

        var d = i.Detection;
        stdout.WriteLine($"Package      : {path}");
        stdout.WriteLine($"Name         : {d.Name}");
        stdout.WriteLine($"SetupFile    : {d.SetupFile}");
        stdout.WriteLine($"ToolVersion  : {d.ToolVersion}");
        stdout.WriteLine($"Unencrypted  : {d.UnencryptedContentSize:N0} bytes");
        stdout.WriteLine($"Encrypted    : {i.EncryptedSizeBytes:N0} bytes");
        if (d.MsiInfo is { } msi)
        {
            stdout.WriteLine($"MsiProductCode : {msi.MsiProductCode}");
            stdout.WriteLine($"MsiVersion     : {msi.MsiProductVersion}");
            stdout.WriteLine($"MsiPublisher   : {msi.MsiPublisher}");
            stdout.WriteLine($"MsiContext     : {msi.MsiExecutionContext} (0=machine 1=user 2=dual)");
        }
        stdout.WriteLine("Checks:");
        stdout.WriteLine($"  HMAC   {(i.MacValid ? "OK" : "FAIL")}");
        stdout.WriteLine($"  Digest {(i.DigestValid ? "OK" : "FAIL")}");
        stdout.WriteLine($"  Size   {(i.SizeValid ? "OK" : "FAIL")}");
        stdout.WriteLine($"Payload: {i.PayloadEntryCount} entr{(i.PayloadEntryCount == 1 ? "y" : "ies")}");
        foreach (var e in i.PayloadEntries.Take(MaxListedEntries))
            stdout.WriteLine("  " + e);
        if (i.PayloadEntryCount > MaxListedEntries)
            stdout.WriteLine($"  … and {i.PayloadEntryCount - MaxListedEntries} more");
        stdout.WriteLine($"Verdict: {(i.IsValid ? "VALID" : "INVALID")}");
        return i.IsValid ? 0 : 1;
    }

    // ── extract ─────────────────────────────────────────────────────────────

    private static int Extract(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var (opts, positionals) = Parse(args, expectedPositionals: 1);
        if (positionals.Count != 1) throw new CliUsageException("extract needs exactly one <package.intunewin>");
        var package = positionals[0];
        if (!File.Exists(package)) throw new CliUsageException($"file not found: {package}");

        var outDir = opts.GetValueOrDefault("--output") ?? ".";
        var dest = Path.Combine(outDir, Path.GetFileNameWithoutExtension(package) + "-payload.zip");
        if (File.Exists(dest) && !opts.ContainsKey("--overwrite"))
        {
            stderr.WriteLine($"error: {dest} already exists (use --overwrite).");
            return 1;
        }
        Directory.CreateDirectory(outDir);

        var r = PackageInspector.ExtractPayloadZip(package, dest);
        if (!r.MacValid)
        {
            // Verify-then-decrypt: nothing was written.
            stderr.WriteLine("error: HMAC validation failed — refusing to extract a tampered payload.");
            return 1;
        }
        stdout.WriteLine(dest);
        if (!r.IsValid)
        {
            stderr.WriteLine("warning: payload extracted but digest/size did not match Detection.xml.");
            return 1;
        }
        return 0;
    }

    // ── signing options ─────────────────────────────────────────────────────

    /// <summary>
    /// Signing is enabled by presence of a mode flag (--pfx, --pkcs11-module, or
    /// the --azure-* trio); null means "don't sign". Secrets come only from env:
    /// WRAPTUNE_SIGN_SECRET (PFX password / PKCS#11 PIN) or WRAPTUNE_AZURE_TOKEN
    /// (pasted token; blank = auto-fetch via the Azure CLI).
    /// </summary>
    internal static SigningOptions? BuildSigningOptions(
        IReadOnlyDictionary<string, string> opts, Func<string, string?> env)
    {
        bool azure = opts.ContainsKey("--azure-endpoint") || opts.ContainsKey("--azure-account")
                     || opts.ContainsKey("--azure-profile");
        bool pkcs11 = opts.ContainsKey("--pkcs11-module");
        bool pfx = opts.ContainsKey("--pfx");

        int modes = (azure ? 1 : 0) + (pkcs11 ? 1 : 0) + (pfx ? 1 : 0);
        if (modes == 0) return null;
        if (modes > 1)
            throw new CliUsageException("pick one signing mode: --pfx, --pkcs11-module, or --azure-endpoint/--azure-account/--azure-profile");

        var mode = azure ? CertMode.TrustedSigning : pkcs11 ? CertMode.Pkcs11 : CertMode.Pfx;
        // In TrustedSigning mode Secret carries the pasted Azure token (WrapTune
        // convention — PayloadSigner routes it to the engine's access token).
        var secret = mode == CertMode.TrustedSigning ? env(AzureTokenEnvVar) : env(SecretEnvVar);

        return new SigningOptions
        {
            CertMode = mode,
            PfxPath = opts.GetValueOrDefault("--pfx"),
            Pkcs11ModulePath = opts.GetValueOrDefault("--pkcs11-module"),
            Pkcs11CertThumbprint = opts.GetValueOrDefault("--pkcs11-thumbprint"),
            TrustedSigningEndpoint = opts.GetValueOrDefault("--azure-endpoint"),
            TrustedSigningAccount = opts.GetValueOrDefault("--azure-account"),
            TrustedSigningProfile = opts.GetValueOrDefault("--azure-profile"),
            TimestampUrl = opts.GetValueOrDefault("--timestamp-url"),
            Description = opts.GetValueOrDefault("--description"),
            Url = opts.GetValueOrDefault("--sign-url"),
            SignAllSignableFiles = opts.ContainsKey("--sign-all"),
            Secret = string.IsNullOrEmpty(secret) ? null : secret,
        };
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private static (Dictionary<string, string> Options, List<string> Positionals) Parse(
        string[] args, int expectedPositionals)
    {
        var opts = new Dictionary<string, string>();
        var positionals = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith('-'))
            {
                if (positionals.Count >= expectedPositionals)
                    throw new CliUsageException($"unexpected argument '{token}'");
                positionals.Add(token);
                continue;
            }
            var flag = Aliases.GetValueOrDefault(token, token);
            if (!KnownFlags.Contains(flag))
                throw new CliUsageException($"unknown option '{token}'");
            if (BoolFlags.Contains(flag))
            {
                opts[flag] = "true";
                continue;
            }
            if (i + 1 >= args.Length)
                throw new CliUsageException($"option '{token}' needs a value");
            opts[flag] = args[++i];
        }
        return (opts, positionals);
    }

    private static string Require(IReadOnlyDictionary<string, string> opts, string flag)
        => opts.GetValueOrDefault(flag) ?? throw new CliUsageException($"required option {flag} is missing");

    private static string SinglePackageArg(string[] args)
    {
        var (_, positionals) = Parse(args, expectedPositionals: 1);
        if (positionals.Count != 1) throw new CliUsageException("expected exactly one <package.intunewin>");
        if (!File.Exists(positionals[0])) throw new CliUsageException($"file not found: {positionals[0]}");
        return positionals[0];
    }

    private static PackageInspection TryInspect(string path)
    {
        try { return PackageInspector.Inspect(path); }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            throw new CliUsageException($"not a readable .intunewin: {ex.Message}");
        }
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    /// <summary>Writes progress lines synchronously — unlike Progress&lt;T&gt;,
    /// which posts to the thread pool and can interleave console output.</summary>
    private sealed class LineWriter(TextWriter writer) : IProgress<string>
    {
        public void Report(string value) => writer.WriteLine(value);
    }

    private const string Usage = """
        wraptune — build, inspect, and extract Microsoft Intune .intunewin packages

        usage:
          wraptune pack -c <source-folder> -s <setup-file> -o <output-folder> [options]
          wraptune inspect <package.intunewin>
          wraptune extract <package.intunewin> [-o <folder>] [--overwrite]

        pack options (flags mirror the official IntuneWinAppUtil where they overlap):
          -c, --source <dir>     folder whose contents get packaged
          -s, --setup <file>     the installer inside the source folder
          -o, --output <dir>     where the .intunewin is written
          -q, --quiet            suppress progress; silently overwrites (official -q)
              --overwrite        replace an existing .intunewin

        payload signing (optional — presence of a mode flag enables it):
              --pfx <path>                             PFX / .p12 file
              --pkcs11-module <path>                   PKCS#11 token / HSM
              --pkcs11-thumbprint <hex>                cert selector (multi-cert tokens)
              --azure-endpoint <host>                  Azure Artifact Signing
              --azure-account <name>                     (all three required)
              --azure-profile <name>
              --timestamp-url <url>                    RFC3161 TSA (Azure mode defaults to Microsoft's)
              --description <text>  --sign-url <url>
              --sign-all                               also sign other signable files

        secrets are read from the environment, never argv:
          WRAPTUNE_SIGN_SECRET    PFX password / PKCS#11 PIN
          WRAPTUNE_AZURE_TOKEN    pasted access token (blank = auto-fetch via az login)

        exit codes: 0 success/valid · 1 failure/invalid · 2 usage error

        """;
}
