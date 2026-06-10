using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WrapTuneMacOS.Services;

namespace WrapTuneMacOS;

/// <summary>
/// The "Update Available" dialog. Drives the UpdateService directly (download →
/// verify → install); on a successful install hand-off it closes the app so the
/// detached helper can swap the bundle and relaunch the new version.
///
/// Note: this dialog is drawn by the RUNNING (old) version, so UI changes here are
/// only visible from the release AFTER the one that ships them.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly UpdateService _service;

    // Parameterless ctor for the Avalonia XAML loader/previewer only.
    public UpdateWindow() : this(new UpdateInfo("0.0.0", "", "", "", ""), new UpdateService()) { }

    public UpdateWindow(UpdateInfo info, UpdateService service)
    {
        _info = info;
        _service = service;
        InitializeComponent();
        TxtTitle.Text = $"WrapTune {info.Version} is available";
        TxtNotes.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "See the release page for details."
            : info.ReleaseNotes;
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        BtnInstall.IsEnabled = false;
        BtnLater.IsEnabled = false;
        Bar.IsVisible = true;
        try
        {
            SetStatus("Downloading…");
            var dmg = await _service.DownloadAsync(
                _info, new Progress<double>(p => Bar.Value = p), CancellationToken.None);
            if (dmg is null) { FailSoft("Download failed. Open the release page to update manually."); return; }

            SetStatus("Verifying…");
            if (!await _service.VerifyAsync(dmg, _info.Version, CancellationToken.None))
            { FailSoft("Couldn’t verify the download. Open the release page to update manually."); return; }

            SetStatus("Installing…");
            var r = await _service.InstallAndRelaunchAsync(dmg, CancellationToken.None);
            if (!r.Success) { FailSoft(r.Detail); return; }

            // The detached helper waits for this process to exit, swaps the bundle,
            // and relaunches the new version.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (Exception ex) { FailSoft(ex.Message); }
    }

    private void FailSoft(string message)
    {
        SetStatus(message);
        Bar.IsVisible = false;
        BtnInstall.IsEnabled = true;
        BtnLater.IsEnabled = true;
    }

    private void SetStatus(string text)
    {
        TxtUpdStatus.Text = text;
        TxtUpdStatus.IsVisible = true;
    }

    private void OnOpenReleasePage(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_info.ReleaseUrl))
            try { _ = Launcher.LaunchUriAsync(new Uri(_info.ReleaseUrl)); } catch { /* best-effort */ }
    }

    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        var s = AppSettings.Load();
        s.SkippedUpdateVersion = _info.Version;
        s.Save();
        Close();
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close();
}
