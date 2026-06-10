using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using WrapTuneMacOS.Packaging;
using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS;

public partial class MainWindow : Window
{
    private readonly IIntuneWinPackager _packager = new IntuneWinWriter();
    private string _theme = "Daylight";
    private CancellationTokenSource? _cts;

    /// <summary>The default RFC3161 timestamp server offered for new installs.</summary>
    private const string DefaultTimestampUrl = "http://timestamp.digicert.com";

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        ApplyTheme(_theme);
        WireDragDrop();
        UpdateSigningUi();
    }

    // ── Settings ────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        var s = AppSettings.Load();
        if (!string.IsNullOrEmpty(s.SourceFolder)) TxtSourceFolder.Text = s.SourceFolder;
        if (!string.IsNullOrEmpty(s.SetupFile)) TxtSetupFile.Text = s.SetupFile;
        if (!string.IsNullOrEmpty(s.OutputFolder)) TxtOutputFolder.Text = s.OutputFolder;
        _theme = string.IsNullOrEmpty(s.Theme) ? "Daylight" : s.Theme;
        ChkOverwrite.IsChecked = s.Overwrite;

        // Signing settings (the password / PIN / token is never persisted).
        ChkSignPayload.IsChecked = s.SignPayload;
        RbTrustedSigning.IsChecked = s.SignCertMode == nameof(CertMode.TrustedSigning);
        RbPkcs11.IsChecked = s.SignCertMode == nameof(CertMode.Pkcs11);
        RbPfx.IsChecked = RbPkcs11.IsChecked != true && RbTrustedSigning.IsChecked != true;
        TxtPfxPath.Text = s.SignPfxPath;
        TxtPkcs11Module.Text = s.SignPkcs11ModulePath;
        TxtPkcs11Thumbprint.Text = s.SignPkcs11CertThumbprint;
        TxtTsEndpoint.Text = s.SignTsEndpoint;
        TxtTsAccount.Text = s.SignTsAccount;
        TxtTsProfile.Text = s.SignTsProfile;
        TxtTimestampUrl.Text = string.IsNullOrEmpty(s.SignTimestampUrl) ? DefaultTimestampUrl : s.SignTimestampUrl;
        TxtSignDescription.Text = s.SignDescription;
        TxtSignUrl.Text = s.SignUrl;
        ChkSignAllFiles.IsChecked = s.SignAllFiles;
        SetSignExpanded(s.SignPayload);
    }

    private void SaveCurrentSettings() => new AppSettings
    {
        SourceFolder = TxtSourceFolder.Text,
        SetupFile = TxtSetupFile.Text,
        OutputFolder = TxtOutputFolder.Text,
        Theme = _theme,
        Overwrite = ChkOverwrite.IsChecked == true,

        SignPayload = ChkSignPayload.IsChecked == true,
        SignCertMode = CurrentCertMode().ToString(),
        SignPfxPath = TxtPfxPath.Text,
        SignPkcs11ModulePath = TxtPkcs11Module.Text,
        SignPkcs11CertThumbprint = TxtPkcs11Thumbprint.Text,
        SignTsEndpoint = TxtTsEndpoint.Text,
        SignTsAccount = TxtTsAccount.Text,
        SignTsProfile = TxtTsProfile.Text,
        SignTimestampUrl = TxtTimestampUrl.Text,
        SignDescription = TxtSignDescription.Text,
        SignUrl = TxtSignUrl.Text,
        SignAllFiles = ChkSignAllFiles.IsChecked == true,
    }.Save();

    private CertMode CurrentCertMode() =>
        RbTrustedSigning.IsChecked == true ? CertMode.TrustedSigning
        : RbPkcs11.IsChecked == true ? CertMode.Pkcs11
        : CertMode.Pfx;

    // ── Theme ─────────────────────────────────────────────────────────────--

    private void ApplyTheme(string name)
    {
        var dark = name == "Midnight";
        if (Application.Current is { } app)
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        _theme = dark ? "Midnight" : "Daylight";
        BtnTheme.Content = dark ? "◑ Light" : "◐ Dark";
    }

    private void BtnTheme_Click(object? sender, RoutedEventArgs e)
    {
        ApplyTheme(_theme == "Midnight" ? "Daylight" : "Midnight");
        SaveCurrentSettings();
    }

    // ── Browse ──────────────────────────────────────────────────────────────

    private async void BtnBrowseSource_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select the source folder containing your setup files", TxtSourceFolder.Text) is { } folder)
        {
            TxtSourceFolder.Text = folder;
            AutoPopulateSetupFile(folder);
        }
    }

    private async void BtnBrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select the output folder for the .intunewin file", TxtOutputFolder.Text) is { } folder)
            TxtOutputFolder.Text = folder;
    }

    private async void BtnBrowseSetup_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the setup file",
            AllowMultiple = false,
            SuggestedStartLocation = await StartLocationAsync(
                Directory.Exists(TxtSourceFolder.Text) ? TxtSourceFolder.Text : null),
            FileTypeFilter =
            [
                new FilePickerFileType("Installers")
                {
                    Patterns = InstallerExtensions.All.Select(x => "*" + x).ToArray(),
                },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (files.Count > 0) TxtSetupFile.Text = files[0].Path.LocalPath;
    }

    private async Task<string?> PickFolderAsync(string title, string? initial)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartLocationAsync(initial),
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private async Task<IStorageFolder?> StartLocationAsync(string? path) =>
        !string.IsNullOrEmpty(path) && Directory.Exists(path)
            ? await StorageProvider.TryGetFolderFromPathAsync(path)
            : null;

    private void AutoPopulateSetupFile(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var installers = Directory.GetFiles(folder).Where(InstallerExtensions.IsInstaller).ToArray();
        TxtSetupFile.Text = installers.Length == 1 ? installers[0] : string.Empty;
    }

    // ── Signing UI ────────────────────────────────────────────────────────--

    private async Task<string?> PickFileAsync(string title, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Files") { Patterns = patterns },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private async void BtnBrowsePfx_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("Select the code-signing certificate", "*.pfx", "*.p12") is { } f)
            TxtPfxPath.Text = f;
    }

    private async void BtnBrowsePkcs11_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("Select the PKCS#11 module", "*.dylib", "*.so") is { } f)
            TxtPkcs11Module.Text = f;
    }

    private void BtnSignToggle_Click(object? sender, RoutedEventArgs e) => SetSignExpanded(!SignBody.IsVisible);

    private void SetSignExpanded(bool expanded)
    {
        SignBody.IsVisible = expanded;
        SignChevron.Text = expanded ? "⌃" : "⌄";
    }

    private void ChkSignPayload_IsCheckedChanged(object? sender, RoutedEventArgs e) => UpdateSigningUi();
    private void RbCertMode_IsCheckedChanged(object? sender, RoutedEventArgs e) => UpdateSigningUi();

    private void UpdateSigningUi()
    {
        SigningFields.IsEnabled = ChkSignPayload.IsChecked == true;
        var mode = CurrentCertMode();
        var ts = mode == CertMode.TrustedSigning;
        PanelPfx.IsVisible = mode == CertMode.Pfx;
        PanelPkcs11.IsVisible = mode == CertMode.Pkcs11;
        PanelTrustedSigning.IsVisible = ts;
        // Artifact Signing carries its token in its own panel, so the shared
        // Password/PIN row doesn't apply. The Timestamp row stays: Artifact Signing
        // certs are short-lived, so a blank URL means the Microsoft TSA, not "skip".
        RowSecret.IsVisible = !ts;
        TxtTimestampUrl.Watermark = ts
            ? "Blank = Microsoft TSA (timestamp.acs.microsoft.com)"
            : "RFC3161 timestamp server (blank to skip)";
        if (ts) UpdateTrustedSigningPrereqs();
    }

    /// <summary>
    /// Walk the user through Azure Artifact Signing setup: live-check the token
    /// source, and flag the RBAC role they'll otherwise hit a 403 on. Signing itself
    /// is in-process — the Azure CLI is the only (optional) external piece.
    /// </summary>
    private void UpdateTrustedSigningPrereqs()
    {
        var hasAz = SignerLocator.LocateAzureCli() is not null;
        TxtTsPrereq.Text =
            (hasAz ? "✓  Azure CLI found — sign in first:  az login"
                   : "⚠  Azure CLI not found — install it and run  az login,  or paste a token below") + "\n" +
            "•  Your Azure identity needs the “Artifact Signing Certificate Profile Signer” role on the account (otherwise signing returns 403).";
    }

    private SigningOptions BuildSigningOptions()
    {
        var mode = CurrentCertMode();
        // The token field lives in the Trusted Signing panel; the Password/PIN field
        // serves the other modes. Both are transient and never persisted.
        var secret = mode == CertMode.TrustedSigning
            ? NullIfEmpty(TxtTsToken.Text)
            : NullIfEmpty(TxtSecret.Text);   // not trimmed — passwords may have spaces
        return new SigningOptions
        {
            CertMode = mode,
            PfxPath = NullIfBlank(TxtPfxPath.Text),
            Pkcs11ModulePath = NullIfBlank(TxtPkcs11Module.Text),
            Pkcs11CertThumbprint = NullIfBlank(TxtPkcs11Thumbprint.Text),
            TrustedSigningEndpoint = NullIfBlank(TxtTsEndpoint.Text),
            TrustedSigningAccount = NullIfBlank(TxtTsAccount.Text),
            TrustedSigningProfile = NullIfBlank(TxtTsProfile.Text),
            TimestampUrl = NullIfBlank(TxtTimestampUrl.Text),
            Description = NullIfBlank(TxtSignDescription.Text),
            Url = NullIfBlank(TxtSignUrl.Text),
            SignAllSignableFiles = ChkSignAllFiles.IsChecked == true,
            Secret = secret,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ── Package ───────────────────────────────────────────────────────────--

    private string? ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(TxtSourceFolder.Text) || !Directory.Exists(TxtSourceFolder.Text))
            return "Source folder is missing or does not exist.";
        if (string.IsNullOrWhiteSpace(TxtSetupFile.Text) || !File.Exists(TxtSetupFile.Text))
            return "Setup file is missing or does not exist.";
        if (string.IsNullOrWhiteSpace(TxtOutputFolder.Text))
            return "Output folder is not specified.";
        return ValidateSigningInputs();
    }

    private string? ValidateSigningInputs()
    {
        if (ChkSignPayload.IsChecked != true) return null;

        if (RbTrustedSigning.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(TxtTsEndpoint.Text))
                return "Azure Trusted Signing endpoint is required.";
            if (string.IsNullOrWhiteSpace(TxtTsAccount.Text))
                return "Azure Trusted Signing account is required.";
            if (string.IsNullOrWhiteSpace(TxtTsProfile.Text))
                return "Azure Trusted Signing certificate profile is required.";
            if (string.IsNullOrEmpty(TxtTsToken.Text) && SignerLocator.LocateAzureCli() is null)
                return SignerLocator.AzureCliHint;
        }
        else if (RbPkcs11.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(TxtPkcs11Module.Text) || !File.Exists(TxtPkcs11Module.Text))
                return "PKCS#11 module path is missing or does not exist.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(TxtPfxPath.Text) || !File.Exists(TxtPfxPath.Text))
                return "Signing certificate (.pfx) is missing or does not exist.";
            if (string.IsNullOrEmpty(TxtSecret.Text))
                return "The certificate password is required to sign.";
        }

        if (ChkSignAllFiles.IsChecked != true && !SignableExtensions.IsSignable(TxtSetupFile.Text!))
            return "The setup file type can't be Authenticode-signed (.cmd/.bat). " +
                   "Enable \"sign all signable files\" or pick a signable setup file.";

        return null;
    }

    private async void BtnPackage_Click(object? sender, RoutedEventArgs e)
    {
        TxtOutput.Text = string.Empty;

        if (ValidateInputs() is { } error)
        {
            // Echo to the log too — the status bar can truncate, and actionable
            // messages (e.g. the "brew install osslsigncode" hint) must stay readable.
            AppendOutput(error);
            SetStatus(error, "Error");
            return;
        }

        SaveCurrentSettings();

        BtnOpenOutput.IsVisible = false;
        BtnPackage.IsEnabled = false;

        // Progress<T> created on the UI thread marshals callbacks back to it.
        var progress = new Progress<string>(AppendOutput);
        _cts = new CancellationTokenSource();

        try
        {
            // Optionally Authenticode-sign the payload in place, before wrapping.
            // This runs entirely outside the .intunewin engine; if it fails we abort
            // rather than ship an unsigned package.
            if (ChkSignPayload.IsChecked == true)
            {
                SetStatus("Signing payload…", "Accent");
                var options = BuildSigningOptions();
                var signer = PayloadSigner.TryCreate(options, out var locateError);
                if (signer is null)
                {
                    Fail(locateError!);
                    return;
                }
                var signed = await signer.SignAsync(
                    TxtSourceFolder.Text!.Trim(), TxtSetupFile.Text!.Trim(), options, progress, _cts.Token);
                if (!signed.Success)
                {
                    Fail("Signing failed — " + signed.Error);
                    return;
                }
            }

            SetStatus("Packaging…", "Accent");
            var request = new PackageRequest(
                TxtSourceFolder.Text!.Trim(),
                TxtSetupFile.Text!.Trim(),
                TxtOutputFolder.Text!.Trim(),
                ChkOverwrite.IsChecked == true);

            var result = await _packager.PackageAsync(request, progress, _cts.Token);

            if (result.Success)
            {
                AppendOutput("Package created successfully!");
                SetStatus("Done — .intunewin file created.", "Success");
                BtnOpenOutput.IsVisible = true;
            }
            else
            {
                AppendOutput("ERROR  " + result.Error);
                SetStatus("Failed — " + result.Error, "Error");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", "Error");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            BtnPackage.IsEnabled = true;
        }

        void Fail(string message)
        {
            AppendOutput("ERROR  " + message);
            SetStatus(message, "Error");
        }
    }

    private async void BtnOpenOutput_Click(object? sender, RoutedEventArgs e)
    {
        var dir = TxtOutputFolder.Text;
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
    }

    // ── Output log / status ──────────────────────────────────────────────--

    private void AppendOutput(string line)
    {
        TxtOutput.Text += line + Environment.NewLine;
        TxtOutput.CaretIndex = TxtOutput.Text?.Length ?? 0;
    }

    private void SetStatus(string text, string brushKey)
    {
        TxtStatus.Text = text;
        if (this.TryFindResource(brushKey, ActualThemeVariant, out var res) && res is IBrush brush)
            TxtStatus.Foreground = brush;
    }

    // ── Drag and drop ───────────────────────────────────────────────────────

    private void WireDragDrop()
    {
        TxtSourceFolder.AddHandler(DragDrop.DragOverEvent, OnFolderDragOver);
        TxtSourceFolder.AddHandler(DragDrop.DropEvent, OnSourceDrop);
        TxtSetupFile.AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        TxtSetupFile.AddHandler(DragDrop.DropEvent, OnFileDrop);
        TxtOutputFolder.AddHandler(DragDrop.DragOverEvent, OnFolderDragOver);
        TxtOutputFolder.AddHandler(DragDrop.DropEvent, OnFolderDrop);
    }

    private void OnFolderDragOver(object? sender, DragEventArgs e) => SetDragEffect(e, foldersOnly: true);
    private void OnFileDragOver(object? sender, DragEventArgs e) => SetDragEffect(e, foldersOnly: false);

    private static void SetDragEffect(DragEventArgs e, bool foldersOnly)
    {
        e.DragEffects = GetSingleDropPath(e, foldersOnly) is not null
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSourceDrop(object? sender, DragEventArgs e)
    {
        if (GetSingleDropPath(e, foldersOnly: true) is { } path)
        {
            TxtSourceFolder.Text = path;
            AutoPopulateSetupFile(path);
        }
    }

    private void OnFolderDrop(object? sender, DragEventArgs e)
    {
        if (GetSingleDropPath(e, foldersOnly: true) is { } path && sender is TextBox tb) tb.Text = path;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (GetSingleDropPath(e, foldersOnly: false) is { } path && sender is TextBox tb) tb.Text = path;
    }

    private static string? GetSingleDropPath(DragEventArgs e, bool foldersOnly)
    {
        var files = e.Data.GetFiles()?.ToArray();
        if (files is not { Length: 1 }) return null;
        var path = files[0].Path.LocalPath;
        var isDir = Directory.Exists(path);
        return foldersOnly == isDir ? path : null;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────--

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
