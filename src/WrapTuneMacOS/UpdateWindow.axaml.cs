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

    /// <summary>Cancels an in-flight download/verify when the window closes.</summary>
    private CancellationTokenSource? _cts;

    /// <summary>True from the moment the swap helper is about to launch until
    /// shutdown — the app is committed and the window must not close under it.</summary>
    private bool _swapping;

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
        // Skip must go dark too: skipping during an install would let the
        // background install finish and force-quit the app "for no reason".
        BtnInstall.IsEnabled = false;
        BtnLater.IsEnabled = false;
        BtnSkip.IsEnabled = false;
        Bar.IsVisible = true;
        _cts = new CancellationTokenSource();
        try
        {
            SetStatus("Downloading…");
            var dmg = await _service.DownloadAsync(
                _info, new Progress<double>(p => Bar.Value = p), _cts.Token);
            if (dmg is null) { FailSoft("Download failed. Open the release page to update manually."); return; }

            SetStatus("Verifying…");
            if (!await _service.VerifyAsync(dmg, _info.Version, _cts.Token))
            { FailSoft("Couldn’t verify the download. Open the release page to update manually."); return; }

            // Point of no return: the detached swap helper is about to launch, so
            // closing the window must no longer cancel anything.
            _swapping = true;
            SetStatus("Installing…");
            var r = await _service.InstallAndRelaunchAsync(dmg, CancellationToken.None);
            if (!r.Success) { _swapping = false; FailSoft(r.Detail); return; }

            // The detached helper waits for this process to exit, swaps the bundle,
            // and relaunches the new version.
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (OperationCanceledException) { /* window closed mid-download — nothing to undo */ }
        catch (Exception ex) { FailSoft(ex.Message); }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void FailSoft(string message)
    {
        SetStatus(message);
        Bar.IsVisible = false;
        BtnInstall.IsEnabled = true;
        BtnLater.IsEnabled = true;
        BtnSkip.IsEnabled = true;
    }

    private void SetStatus(string text)
    {
        TxtUpdStatus.Text = text;
        TxtUpdStatus.IsVisible = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_swapping)
        {
            // Seconds away from relaunching on the new version — don't let a
            // close race the swap helper.
            e.Cancel = true;
            return;
        }
        _cts?.Cancel();
        base.OnClosing(e);
    }

    private void OnOpenReleasePage(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_info.ReleaseUrl))
            try { _ = Launcher.LaunchUriAsync(new Uri(_info.ReleaseUrl)); } catch { /* best-effort */ }
    }

    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        AppSettings.Update(s => s.SkippedUpdateVersion = _info.Version);
        Close();
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close();
}
