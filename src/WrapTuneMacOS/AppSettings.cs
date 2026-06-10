using System.Text.Json;

namespace WrapTuneMacOS;

/// <summary>
/// Per-user settings at
/// <c>~/Library/Application Support/WrapTuneMacOS/settings.json</c>.
/// Adapted from WrapTune; the Windows-only ExePath and Catalog fields are gone
/// (the engine is built in and catalog signing isn't supported on macOS).
/// </summary>
public sealed class AppSettings
{
    public string? SourceFolder { get; set; }
    public string? SetupFile { get; set; }
    public string? OutputFolder { get; set; }

    /// <summary>"Daylight" (default) or "Midnight".</summary>
    public string Theme { get; set; } = "Daylight";

    /// <summary>Persist the Overwrite checkbox across runs.</summary>
    public bool Overwrite { get; set; } = true;

    // ── Code signing (optional). The PFX password / HSM PIN is NEVER persisted. ──

    public bool SignPayload { get; set; }

    /// <summary>"Pfx" (default), "Pkcs11", or "TrustedSigning".</summary>
    public string SignCertMode { get; set; } = "Pfx";

    public string? SignPfxPath { get; set; }
    public string? SignPkcs11ModulePath { get; set; }
    public string? SignPkcs11CertThumbprint { get; set; }
    public string? SignTimestampUrl { get; set; }
    public string? SignDescription { get; set; }
    public string? SignUrl { get; set; }
    public bool SignAllFiles { get; set; }

    // Azure Trusted Signing (non-secret; the access token is never persisted).
    public string? SignTsEndpoint { get; set; }
    public string? SignTsAccount { get; set; }
    public string? SignTsProfile { get; set; }

    // ── In-app updater ──
    /// <summary>ISO-8601 stamp of the last successful update check; null = never.</summary>
    public string? LastUpdateCheckUtc { get; set; }

    /// <summary>Version the user chose to skip from the update dialog.</summary>
    public string? SkippedUpdateVersion { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetSettingsPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            // Known .NET bug: LocalApplicationData can come back empty on some
            // macOS releases. Fall back to the conventional location so we never
            // write settings into the working directory.
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");
        }
        return Path.Combine(baseDir, "WrapTuneMacOS", "settings.json");
    }

    public static AppSettings Load()
    {
        var path = GetSettingsPath();
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var path = GetSettingsPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Best-effort: never crash on exit because settings couldn't be written.
        }
    }
}
