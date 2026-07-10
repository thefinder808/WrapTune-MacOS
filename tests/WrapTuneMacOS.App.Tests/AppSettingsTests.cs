namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Persistence tests for AppSettings, redirected to a throwaway folder via the
/// internal test seam so they never touch the user's real settings file.
/// </summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir;

    public AppSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wraptune-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppSettings.BaseDirOverride = _dir;
    }

    public void Dispose()
    {
        AppSettings.BaseDirOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Save_then_load_round_trips_fields()
    {
        AppSettings.Update(s =>
        {
            s.Theme = "Midnight";
            s.SourceFolder = "/tmp/src";
            s.SkippedUpdateVersion = "9.9.9";
        });

        var loaded = AppSettings.Load();
        Assert.Equal("Midnight", loaded.Theme);
        Assert.Equal("/tmp/src", loaded.SourceFolder);
        Assert.Equal("9.9.9", loaded.SkippedUpdateVersion);
    }

    [Fact]
    public void Concurrent_updates_from_two_threads_lose_neither_write()
    {
        // Models the real race: the UI thread saving window fields while the
        // background update check stamps LastUpdateCheckUtc.
        Parallel.For(0, 2, writer =>
        {
            for (int i = 0; i < 50; i++)
            {
                if (writer == 0)
                    AppSettings.Update(s => s.Theme = "Midnight");
                else
                    AppSettings.Update(s => s.LastUpdateCheckUtc = "stamp-" + i);
            }
        });

        var final = AppSettings.Load();
        Assert.Equal("Midnight", final.Theme);
        Assert.Equal("stamp-49", final.LastUpdateCheckUtc);
    }
}
