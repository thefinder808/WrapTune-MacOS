using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WrapTuneMacOS.Services;

/// <summary>Immutable description of an available release.</summary>
public sealed record UpdateInfo(string Version, string ReleaseNotes, string ReleaseUrl,
                                string AssetName, string AssetUrl);

/// <summary>Outcome of a check: an update + its info, or none, or an error string.</summary>
public sealed record UpdateCheckResult(bool UpdateAvailable, UpdateInfo? Info, string? Error);

/// <summary>Outcome of an install step.</summary>
public sealed record UpdateOpResult(bool Success, string Detail)
{
    public static UpdateOpResult Ok(string detail) => new(true, detail);
    public static UpdateOpResult Fail(string detail) => new(false, detail);
}

/// <summary>
/// The in-app updater, ported from MacSign (same author, Apache-2.0): check GitHub
/// Releases, download the right-arch DMG, verify the <b>.app inside</b> it is
/// Developer-ID signed by our Team ID + notarized (plus the DMG's stapled ticket),
/// then install via a detached swap script and relaunch. Avalonia-free.
///
/// The trust gate inspects the inner app, not the DMG container: release DMGs are
/// notarized + stapled but the image itself is not codesigned, so checking the
/// container's own signature would reject every legitimate release.
/// </summary>
public sealed class UpdateService
{
    /// <summary>The ONLY accepted signer. A downloaded DMG must contain an app
    /// codesigned by this Developer ID Team ID (and notarized) or it is never
    /// installed — this is the root of trust. Team IDs are stable across cert
    /// renewals, so this survives cert rotation.</summary>
    public const string ExpectedTeamId = "Q6LRJQSA42";

    /// <summary>Product identity the downloaded bundle must match — bound in addition
    /// to the Team ID so a different (even validly-signed) app by the same Developer
    /// ID can't install. Bundle id + executable come from the signed CodeDirectory;
    /// the .app name and asset name are structural. All four are checked.</summary>
    public const string ExpectedBundleId = "com.thefinder808.WrapTuneMacOS";
    public const string ExpectedExecutable = "WrapTuneMacOS";
    public const string ExpectedAppName = "WrapTune.app";

    private const string Owner = "thefinder808", Repo = "WrapTune-MacOS";

    private const string Codesign   = "/usr/bin/codesign";
    private const string Spctl      = "/usr/sbin/spctl";
    private const string Hdiutil    = "/usr/bin/hdiutil";
    private const string Ditto      = "/usr/bin/ditto";
    private const string Xcrun      = "/usr/bin/xcrun";
    private const string PlistBuddy = "/usr/libexec/PlistBuddy";

    private readonly HttpClient _http;

    /// <summary>Deadline for the release-check API call (its own linked token —
    /// the client itself must not impose one, see the ctor).</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    // HttpClient.Timeout covers the WHOLE request including a streamed body, so
    // the stock 100 s would abort a DMG download on any slow connection. Requests
    // that need a deadline (the API check) bring their own token instead.
    public UpdateService(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    // ── Version identity ─────────────────────────────────────────────────────

    private static string? _currentVersion;
    private static bool _currentVersionRead;

    /// <summary>
    /// The running app's version, read from the bundle's own (signature-sealed)
    /// Info.plist — the same value build-macos.sh stamps from the release tag.
    /// Null outside a .app bundle (dev builds), which keeps the updater quiet.
    /// </summary>
    public static async Task<string?> CurrentVersionAsync(CancellationToken ct = default)
    {
        if (_currentVersionRead) return _currentVersion;
        var plist = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Info.plist"));
        _currentVersion = File.Exists(plist) ? await ReadShortVersionFromPlistAsync(plist, ct) : null;
        _currentVersionRead = true;
        return _currentVersion;
    }

    // ── Version comparison + asset selection (pure; unit-tested) ─────────────

    /// <summary>True iff <paramref name="latestTag"/> parses to a strictly greater
    /// version than <paramref name="current"/>. Tolerates a leading "v" and trailing
    /// pre-release/build metadata; returns false on any unparseable input.</summary>
    public static bool IsNewer(string latestTag, string? current)
    {
        var l = ParseVersion(latestTag);
        var c = ParseVersion(current);
        return l is not null && c is not null && l > c;
    }

    private static Version? ParseVersion(string? s)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var core = new string(s.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        if (core.Length == 0) return null;
        if (!core.Contains('.')) core += ".0";          // Version needs >=2 parts
        return Version.TryParse(core, out var v) ? v : null;
    }

    /// <summary>Pick the asset whose name EXACTLY matches our release naming for this
    /// version and host architecture (<c>WrapTuneMacOS-&lt;version&gt;-osx-&lt;arch&gt;.dmg</c>),
    /// or null. An exact match — not just an arch suffix — so a stray/misnamed asset
    /// can't be selected.</summary>
    public static string? AssetNameFor(IEnumerable<string> assetNames, string version)
    {
        var expected = $"WrapTuneMacOS-{version}-{ArchSuffix()}.dmg";
        return assetNames.FirstOrDefault(n => string.Equals(n, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string ArchSuffix() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "osx-arm64",
        Architecture.X64   => "osx-x64",
        _                  => "osx-x64",
    };

    /// <summary>Daily throttle for the on-launch background check. Checks when never
    /// checked before, when the stamp is unparseable, or when 24 h have passed.</summary>
    public static bool ShouldAutoCheck(string? lastIso, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(lastIso)) return true;
        return !DateTime.TryParse(lastIso, null,
                   System.Globalization.DateTimeStyles.AdjustToUniversal, out var last)
               || nowUtc - last > TimeSpan.FromHours(24);
    }

    // ── Check ─────────────────────────────────────────────────────────────────

    /// <summary>Query the latest stable release (the endpoint excludes drafts +
    /// prereleases), compare to the running version, and pick the host-arch asset.
    /// Never throws — network/parse failures come back as an Error.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            var current = await CurrentVersionAsync(ct);
            if (current is null)
                return new UpdateCheckResult(false, null,
                    "Couldn't determine the installed version (development build?).");

            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("WrapTuneMacOS", current));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(CheckTimeout);

            using var resp = await _http.SendAsync(req, deadline.Token);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                // Unauthenticated GitHub API calls are capped per IP; behind a
                // shared NAT this is far likelier than a real outage.
                return new UpdateCheckResult(false, null,
                    "GitHub returned 403 — likely the API rate limit; try again later.");
            if (!resp.IsSuccessStatusCode)
                return new UpdateCheckResult(false, null, $"GitHub returned {(int)resp.StatusCode}.");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(deadline.Token));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!IsNewer(tag, current))
                return new UpdateCheckResult(false, null, null);

            var version = tag.TrimStart('v', 'V');
            var names = root.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("name").GetString() ?? "").ToList();
            var assetName = AssetNameFor(names, version);
            if (assetName is null)
                return new UpdateCheckResult(false, null, "No matching .dmg asset for this Mac's architecture.");

            var asset = root.GetProperty("assets").EnumerateArray()
                .First(a => a.GetProperty("name").GetString() == assetName);
            var info = new UpdateInfo(
                Version: version,
                ReleaseNotes: root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                ReleaseUrl: root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                AssetName: assetName,
                AssetUrl: asset.GetProperty("browser_download_url").GetString() ?? "");
            return new UpdateCheckResult(true, info, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new UpdateCheckResult(false, null, "GitHub didn't respond within 30 seconds."); }
        catch (OperationCanceledException) { return new UpdateCheckResult(false, null, "Canceled."); }
        catch (Exception ex) { return new UpdateCheckResult(false, null, ex.Message); }
    }

    // ── Verify (the trust gate) ───────────────────────────────────────────────

    /// <summary>A download installs only if the <b>.app inside</b> the DMG is
    /// Developer-ID signed by our Team ID AND notarized (spctl accepts only notarized
    /// Developer ID code), its signed bundle id / executable / version match, and the
    /// .dmg itself carries a stapled notarization ticket. Any failure ⇒ false ⇒ the
    /// caller never installs it. Always detaches.</summary>
    public async Task<bool> VerifyAsync(string dmgPath, string expectedVersion, CancellationToken ct)
    {
        if (!File.Exists(dmgPath) ||
            !string.Equals(Path.GetExtension(dmgPath), ".dmg", StringComparison.OrdinalIgnoreCase))
            return false;

        var mount = Path.Combine(Path.GetTempPath(), "wraptune-verify-" + Guid.NewGuid().ToString("N"));
        bool attached = false;
        try
        {
            var att = await RunAsync(Hdiutil,
                ["attach", "-nobrowse", "-readonly", "-mountpoint", mount, dmgPath], ct);
            if (att.ExitCode != 0) return false;
            attached = true;

            // Require EXACTLY ONE top-level .app named WrapTune.app — no "first of
            // many" ambiguity, and the same invariant the install step enforces (so
            // verify and install can't pick different bundles).
            var apps = Directory.Exists(mount)
                ? Directory.EnumerateDirectories(mount, "*.app").ToList()
                : [];
            if (apps.Count != 1) return false;
            var app = apps[0];
            if (!string.Equals(Path.GetFileName(app), ExpectedAppName, StringComparison.Ordinal)) return false;

            // codesign -d -vvv → signed identity fields (writes to stderr).
            var d = await RunAsync(Codesign, ["-d", "-vvv", app], ct);
            var info = d.StdOut + "\n" + d.StdErr;
            bool identityOk =
                string.Equals(Match(info, @"^TeamIdentifier=(.+)$"), ExpectedTeamId, StringComparison.Ordinal)
                && string.Equals(Match(info, @"^Identifier=(.+)$"), ExpectedBundleId, StringComparison.Ordinal)
                && string.Equals(Path.GetFileName(Match(info, @"^Executable=(.+)$") ?? ""),
                                 ExpectedExecutable, StringComparison.Ordinal);

            // Signature must verify, and Gatekeeper must accept (notarized Developer ID).
            var verify = await RunAsync(Codesign, ["--verify", "--deep", "--strict", "--verbose=2", app], ct);
            var spctl = await RunAsync(Spctl, ["--assess", "--type", "exec", "-vv", app], ct);

            // Bind to the advertised version: CFBundleShortVersionString must equal it.
            // The Info.plist is sealed by the (verified) signature, so this is
            // tamper-evident.
            bool versionOk = string.Equals(
                await ReadShortVersionFromPlistAsync(Path.Combine(app, "Contents", "Info.plist"), ct),
                expectedVersion, StringComparison.Ordinal);

            // Defense in depth: the downloaded .dmg must itself be a stapled release.
            // (The app is not stapled — the ticket lives on the image.)
            var staple = await RunAsync(Xcrun, ["stapler", "validate", dmgPath], ct);

            return identityOk && verify.ExitCode == 0 && spctl.ExitCode == 0
                && versionOk && staple.ExitCode == 0;
        }
        finally
        {
            if (attached) await RunAsync(Hdiutil, ["detach", mount, "-force"], CancellationToken.None);
            try { if (Directory.Exists(mount)) Directory.Delete(mount); } catch { /* best-effort */ }
        }
    }

    private static async Task<string?> ReadShortVersionFromPlistAsync(string plist, CancellationToken ct)
    {
        var r = await RunAsync(PlistBuddy, ["-c", "Print :CFBundleShortVersionString", plist], ct);
        var v = r.StdOut.Trim();
        return r.ExitCode == 0 && v.Length > 0 ? v : null;
    }

    private static string? Match(string text, string linePattern)
    {
        var m = Regex.Match(text, linePattern, RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // ── Download ─────────────────────────────────────────────────────────────

    /// <summary>Download the asset to a temp .dmg, reporting 0..1 progress when the
    /// server sends a Content-Length. Returns the path, or null on failure (the
    /// partial temp file is best-effort deleted so retries don't orphan).</summary>
    public async Task<string?> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        // A prior attempt whose verify/install bailed leaves its .dmg behind; clear
        // those first so abandoned downloads can't pile up (the successful install
        // path deletes its own DMG via the swap script).
        PruneStaleDownloads(Path.GetTempPath());

        string? dest = null;
        try
        {
            dest = Path.Combine(Path.GetTempPath(),
                $"wraptune-update-{Guid.NewGuid():N}-{info.AssetName}");
            using var resp = await _http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) { TryDelete(dest); return null; }

            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0) progress?.Report((double)read / total.Value);
            }
            return dest;
        }
        catch (OperationCanceledException)
        {
            // User cancellation is not a download failure — clean up and let the
            // caller's cancellation handling take it from here.
            if (dest is not null) TryDelete(dest);
            throw;
        }
        catch { if (dest is not null) TryDelete(dest); return null; }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best-effort */ } }

    /// <summary>Best-effort delete of leftover download artifacts
    /// (<c>wraptune-update-*</c>) from prior, abandoned update attempts.</summary>
    public static void PruneStaleDownloads(string tempDir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(tempDir, "wraptune-update-*"))
                TryDelete(f);
        }
        catch { /* best-effort */ }
    }

    // ── Install + relaunch ───────────────────────────────────────────────────

    /// <summary>Resolve the .app bundle from the executable's base dir
    /// (…/Contents/MacOS → two levels up).</summary>
    public static string InstalledAppPathFrom(string baseDir)
        => Path.GetFullPath(Path.Combine(baseDir, "..", ".."));

    private static bool DirWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".wraptune-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, ""); File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Mount the (already-verified) DMG, stage the new app, write a detached
    /// helper that waits for us to exit, atomically swaps the bundle, and relaunches.
    /// Returns a failure (without quitting) if the install dir isn't writable.</summary>
    public Task<UpdateOpResult> InstallAndRelaunchAsync(string dmgPath, CancellationToken ct)
        => InstallAndRelaunchAsync(dmgPath, InstalledAppPathFrom(AppContext.BaseDirectory), ct);

    // installedAppPath is injectable for tests.
    public async Task<UpdateOpResult> InstallAndRelaunchAsync(string dmgPath, string installedAppPath, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(installedAppPath) ?? "/Applications";
        if (!DirWritable(parent))
            return UpdateOpResult.Fail(
                "WrapTune can't write to its install folder. Drag the new WrapTune to Applications to finish updating.");

        var stamp = Guid.NewGuid().ToString("N")[..8];
        var mount = Path.Combine(Path.GetTempPath(), $"wraptune-upd-mnt-{stamp}");
        var staged = Path.Combine(Path.GetTempPath(), $"wraptune-upd-app-{stamp}");
        var script = Path.Combine(Path.GetTempPath(), $"wraptune-upd-{stamp}.sh");
        bool attached = false, launched = false;
        try
        {
            var att = await RunAsync(Hdiutil,
                ["attach", "-nobrowse", "-readonly", "-mountpoint", mount, dmgPath], ct);
            if (att.ExitCode != 0) return UpdateOpResult.Fail("Couldn't mount the downloaded disk image.");
            attached = true;

            // Same invariant as VerifyAsync: exactly one top-level WrapTune.app — so the
            // bundle we install is the one that was verified, never a different
            // "first of many".
            var apps = Directory.EnumerateDirectories(mount, "*.app").ToList();
            var src = apps.Count == 1
                && string.Equals(Path.GetFileName(apps[0]), ExpectedAppName, StringComparison.Ordinal)
                ? apps[0] : null;
            if (src is null) return UpdateOpResult.Fail("The disk image must contain exactly one WrapTune.app.");

            Directory.CreateDirectory(staged);
            var stagedApp = Path.Combine(staged, Path.GetFileName(src));
            var dit = await RunAsync(Ditto, [src, stagedApp], ct);
            if (!(dit.ExitCode == 0)) return UpdateOpResult.Fail("Couldn't copy the new version out of the disk image.");

            var det = await RunAsync(Hdiutil, ["detach", mount, "-force"], ct);
            attached = det.ExitCode != 0;   // if detach failed, the finally retries

            await File.WriteAllTextAsync(script, BuildSwapScript(Environment.ProcessId), ct);

            // Fire-and-forget detached — this must outlive us. ArgumentList
            // shell-escapes each path, so no path can inject shell. It reparents to
            // launchd when we quit.
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            psi.ArgumentList.Add(script);           // $0 (self-deletes)
            psi.ArgumentList.Add(installedAppPath); // $1
            psi.ArgumentList.Add(stagedApp);        // $2
            psi.ArgumentList.Add(staged);           // $3
            psi.ArgumentList.Add(dmgPath);          // $4
            Process.Start(psi);
            launched = true;
            return UpdateOpResult.Ok("WrapTune will relaunch on the new version.");
        }
        finally
        {
            if (attached) await RunAsync(Hdiutil, ["detach", mount, "-force"], CancellationToken.None);
            if (!launched)
            {
                try { if (Directory.Exists(staged)) Directory.Delete(staged, true); } catch { /* best-effort */ }
                try { if (File.Exists(script)) File.Delete(script); } catch { /* best-effort */ }
                try { if (File.Exists(dmgPath)) File.Delete(dmgPath); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>The detached swap+relaunch script. Paths are passed as positional args
    /// ($1=installed .app, $2=staged .app, $3=staged dir, $4=downloaded .dmg) so
    /// NOTHING is interpolated into the shell — only the integer pid is. Crash-safe:
    /// the old bundle is renamed aside before the new one moves in, with rollback on
    /// failure.</summary>
    public static string BuildSwapScript(int pid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine($"while kill -0 {pid} 2>/dev/null; do sleep 0.2; done");
        sb.AppendLine("/bin/rm -rf \"$1.new\" \"$1.old\"");
        sb.AppendLine("/usr/bin/ditto \"$2\" \"$1.new\" || exit 1");
        sb.AppendLine("if [ -e \"$1\" ]; then /bin/mv \"$1\" \"$1.old\"; fi");
        sb.AppendLine("if /bin/mv \"$1.new\" \"$1\"; then /bin/rm -rf \"$1.old\"; else [ -e \"$1.old\" ] && /bin/mv \"$1.old\" \"$1\"; exit 1; fi");
        sb.AppendLine("/bin/rm -rf \"$3\" \"$4\"");
        sb.AppendLine("/usr/bin/open \"$1\"");
        sb.AppendLine("/bin/rm -f \"$0\"");
        return sb.ToString();
    }

    // ── Process helper ───────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Disposing only releases handles; don't leave hdiutil/codesign running.
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
