using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WrapTuneMacOS.Services;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Offline tests for the updater's pure seams: version comparison, exact asset
/// selection, the daily auto-check throttle, and the swap script's safety
/// properties. The network/codesign/hdiutil paths are exercised manually against
/// real releases (and were proven in MacSign, where this updater originates).
/// </summary>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.3.0", "1.2.0", true)]
    [InlineData("1.3.0", "1.2.0", true)]
    [InlineData("v1.2.0", "1.2.0", false)]
    [InlineData("v1.1.9", "1.2.0", false)]
    [InlineData("v2.0.0-rc.1", "1.2.0", true)]   // pre-release suffix tolerated
    [InlineData("v1.3.0", "dev", false)]          // unparseable current → never prompt
    [InlineData("garbage", "1.2.0", false)]       // unparseable latest → never prompt
    [InlineData("v1.3.0", null, false)]           // no version (dev build) → never prompt
    public void IsNewer_compares_release_tags_safely(string latest, string? current, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewer(latest, current));

    [Fact]
    public void AssetNameFor_requires_an_exact_name_match_for_this_arch()
    {
        string[] assets =
        [
            "WrapTuneMacOS-1.3.0-osx-arm64.dmg",
            "WrapTuneMacOS-1.3.0-osx-x64.dmg",
            "WrapTuneMacOS-1.3.0-osx-arm64.dmg.sha256",   // near-miss must not match
            "SomethingElse-1.3.0-osx-arm64.dmg",
        ];

        var picked = UpdateService.AssetNameFor(assets, "1.3.0");
        Assert.NotNull(picked);
        Assert.Matches(@"^WrapTuneMacOS-1\.3\.0-osx-(arm64|x64)\.dmg$", picked);

        // A different version yields nothing rather than a wrong asset.
        Assert.Null(UpdateService.AssetNameFor(assets, "1.4.0"));
    }

    [Fact]
    public void ShouldAutoCheck_throttles_to_once_a_day()
    {
        var now = new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(UpdateService.ShouldAutoCheck(null, now));                       // never checked
        Assert.True(UpdateService.ShouldAutoCheck("not-a-date", now));               // bad stamp → check
        Assert.True(UpdateService.ShouldAutoCheck(now.AddHours(-25).ToString("o"), now));
        Assert.False(UpdateService.ShouldAutoCheck(now.AddHours(-1).ToString("o"), now));
    }

    [Fact]
    public void SwapScript_waits_for_our_exit_and_rolls_back_on_failure()
    {
        var script = UpdateService.BuildSwapScript(12345);

        Assert.Contains("while kill -0 12345", script);          // waits for the old app to quit
        Assert.Contains("/usr/bin/ditto \"$2\" \"$1.new\"", script);
        Assert.Contains("/bin/mv \"$1.old\" \"$1\"", script);    // rollback path exists
        Assert.Contains("/usr/bin/open \"$1\"", script);         // relaunches the swapped bundle
        Assert.Contains("/bin/rm -f \"$0\"", script);            // self-deletes
        Assert.DoesNotContain("$installedAppPath", script);      // nothing interpolated but the pid
    }

    [Fact]
    public void InstalledAppPath_resolves_two_levels_up_from_the_executable_dir()
        => Assert.Equal("/Applications/WrapTune.app",
            UpdateService.InstalledAppPathFrom("/Applications/WrapTune.app/Contents/MacOS"));

    [AvaloniaFact]
    public void UpdateWindow_loads_and_shows_the_offered_version()
    {
        var info = new UpdateInfo("9.9.9", "Notes here", "https://example.test/rel", "x.dmg", "https://example.test/x.dmg");
        var w = new UpdateWindow(info, new UpdateService());
        w.Show();

        var title = w.FindControl<TextBlock>("TxtTitle");
        Assert.NotNull(title);
        Assert.Contains("9.9.9", title!.Text);
        Assert.NotNull(w.FindControl<Button>("BtnInstall"));
        Assert.NotNull(w.FindControl<Button>("BtnLater"));
    }

    [AvaloniaFact]
    public void UpdateWindow_names_every_footer_button_so_install_can_disable_them()
    {
        // All four must be reachable from code-behind: an in-flight install has
        // to disable Skip too, or skipping can force-quit the app when the
        // background install completes.
        var info = new UpdateInfo("9.9.9", "", "", "x.dmg", "https://example.test/x.dmg");
        var w = new UpdateWindow(info, new UpdateService());
        w.Show();

        Assert.NotNull(w.FindControl<Button>("BtnInstall"));
        Assert.NotNull(w.FindControl<Button>("BtnLater"));
        Assert.NotNull(w.FindControl<Button>("BtnSkip"));
        Assert.NotNull(w.FindControl<Button>("BtnReleasePage"));
    }

    [Fact]
    public async Task DownloadAsync_propagates_cancellation_instead_of_reporting_failure()
    {
        // A cancelled download must not surface as "Download failed" — the
        // dialog distinguishes user cancellation from real errors.
        var svc = new UpdateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var info = new UpdateInfo("1.0.0", "", "", "x.dmg", "https://example.invalid/x.dmg");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.DownloadAsync(info, progress: null, cts.Token));
    }
}
