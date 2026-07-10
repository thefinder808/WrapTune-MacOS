using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WrapTuneMacOS.Packaging;
using WrapTuneMacOS.Services;
using WrapTuneMacOS.Signing;
using Path = System.IO.Path;   // Avalonia.Controls.Shapes.Path would shadow it

namespace WrapTuneMacOS;

/// <summary>
/// The "flow, grouped fields" main window: one screen, three steps (Files →
/// Sign → Package), swapping to a staged progress view while the engine runs.
/// All engine/signing/settings plumbing is unchanged from the previous UI —
/// this class only re-arranges how it's presented.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IIntuneWinPackager _packager = new IntuneWinWriter();
    private readonly UpdateService _updates = new();
    private string _theme = "Daylight";
    private CancellationTokenSource? _cts;

    /// <summary>The default RFC3161 timestamp server offered for new installs.</summary>
    private const string DefaultTimestampUrl = "http://timestamp.digicert.com";

    // ── flow view state (not persisted) ──
    private bool _running;
    private bool _rawLogVisible;
    private readonly Dictionary<PackageStage, StageRow> _stageRows = new();
    private PackageStage? _activeStage;
    private int? _encryptPercent;
    private readonly System.Diagnostics.Stopwatch _elapsed = new();
    private DispatcherTimer? _elapsedTimer;
    private DispatcherTimer? _spinTimer;
    private double _spinAngle;
    private Arc? _activeSpinner;
    private int _derivedGeneration;
    private DispatcherTimer? _derivedDebounce;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        ApplyTheme(_theme);
        WireDragDrop();
        WireDerivedFields();
        UpdateSigningUi();
        UpdateFlowUi();
        Opened += (_, _) => StartLaunchUpdateCheck();
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
    }

    private void SaveCurrentSettings()
    {
        // Atomic load-mutate-save: fields this window doesn't own (the updater's
        // check stamp / skipped version) must survive a save, including against
        // the background update check writing at the same time.
        AppSettings.Update(s =>
        {
            s.SourceFolder = TxtSourceFolder.Text;
            s.SetupFile = TxtSetupFile.Text;
            s.OutputFolder = TxtOutputFolder.Text;
            s.Theme = _theme;
            s.Overwrite = ChkOverwrite.IsChecked == true;

            s.SignPayload = ChkSignPayload.IsChecked == true;
            s.SignCertMode = CurrentCertMode().ToString();
            s.SignPfxPath = TxtPfxPath.Text;
            s.SignPkcs11ModulePath = TxtPkcs11Module.Text;
            s.SignPkcs11CertThumbprint = TxtPkcs11Thumbprint.Text;
            s.SignTsEndpoint = TxtTsEndpoint.Text;
            s.SignTsAccount = TxtTsAccount.Text;
            s.SignTsProfile = TxtTsProfile.Text;
            s.SignTimestampUrl = TxtTimestampUrl.Text;
            s.SignDescription = TxtSignDescription.Text;
            s.SignUrl = TxtSignUrl.Text;
            s.SignAllFiles = ChkSignAllFiles.IsChecked == true;
        });
    }

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
        if (PanelForm is not null) UpdateFlowUi();   // re-resolve brushes set from code
    }

    private void OnToggleTheme(object? sender, EventArgs e)
    {
        ApplyTheme(_theme == "Midnight" ? "Daylight" : "Midnight");
        SaveCurrentSettings();
    }

    // ── Title bar ─────────────────────────────────────────────────────────--

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    // ── Browse ──────────────────────────────────────────────────────────────

    private async void BtnBrowseSource_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select the source folder containing your setup files", TxtSourceFolder.Text) is { } folder)
            ApplySourceFolder(folder);
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

    /// <summary>Browse/drop of a source folder: auto-detect the setup file and
    /// default the output folder to the source's parent (both stay editable).</summary>
    private void ApplySourceFolder(string folder)
    {
        TxtSourceFolder.Text = folder;
        AutoPopulateSetupFile(folder);
        if (string.IsNullOrWhiteSpace(TxtOutputFolder.Text) &&
            PackagingFlow.DefaultOutputFolder(folder) is { } parent)
            TxtOutputFolder.Text = parent;
    }

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

    private void ChkSignPayload_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        UpdateSigningUi();
        UpdateFlowUi();
    }

    private void RbCertMode_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        UpdateSigningUi();
        UpdateFlowUi();
    }

    private void BtnAdvancedSigning_Click(object? sender, RoutedEventArgs e)
    {
        AdvancedSigning.IsVisible = !AdvancedSigning.IsVisible;
        BtnAdvancedSigning.Content = AdvancedSigning.IsVisible
            ? "advanced — timestamp, description, url ▴"
            : "advanced — timestamp, description, url ▾";
    }

    private void UpdateSigningUi()
    {
        var on = ChkSignPayload.IsChecked == true;
        SignBody.IsVisible = on;

        var mode = CurrentCertMode();
        var ts = mode == CertMode.TrustedSigning;
        PanelPfx.IsVisible = mode == CertMode.Pfx;
        PanelPkcs11.IsVisible = mode == CertMode.Pkcs11;
        PanelTrustedSigning.IsVisible = ts;
        // Artifact Signing certs are short-lived, so a blank URL means the
        // Microsoft TSA, not "skip".
        TxtTimestampUrl.Watermark = ts
            ? "timestamp url — blank = microsoft tsa"
            : "rfc3161 timestamp url";
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
            (hasAz ? "✓ azure cli found — sign in first: az login"
                   : "⚠ azure cli not found — install it and run az login, or paste a token") +
            "\n· your identity needs the “Artifact Signing Certificate Profile Signer” role (else 403)";
    }

    private SigningOptions BuildSigningOptions()
    {
        var mode = CurrentCertMode();
        // Each mode carries its secret in its own field; all are transient and
        // never persisted. In Trusted Signing mode the "secret" is the pasted
        // Azure token (PayloadSigner routes it to the engine's access token).
        var secret = mode switch
        {
            CertMode.TrustedSigning => NullIfEmpty(TxtTsToken.Text),
            CertMode.Pkcs11 => NullIfEmpty(TxtPkcs11Pin.Text),
            _ => NullIfEmpty(TxtSecret.Text),   // not trimmed — passwords may have spaces
        };
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

    // ── Validation ──────────────────────────────────────────────────────────

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

    // ── Flow UI (form state) ───────────────────────────────────────────────--

    private void WireDerivedFields()
    {
        TxtSourceFolder.TextChanged += (_, _) => OnPathsChanged();
        TxtSetupFile.TextChanged += (_, _) => OnPathsChanged();
        TxtOutputFolder.TextChanged += (_, _) => OnPathsChanged();
    }

    private void OnPathsChanged()
    {
        UpdateFlowUi();
        // Debounce the folder-walk / MSI-parse so typing doesn't thrash disk.
        _derivedDebounce?.Stop();
        _derivedDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _derivedDebounce.Tick += DerivedDebounceTick;
        _derivedDebounce.Stop();
        _derivedDebounce.Start();

        void DerivedDebounceTick(object? s, EventArgs e)
        {
            _derivedDebounce!.Tick -= DerivedDebounceTick;
            _derivedDebounce.Stop();
            _ = RefreshDerivedAsync();
        }
    }

    private int PathsSet()
    {
        int n = 0;
        if (!string.IsNullOrWhiteSpace(TxtSourceFolder.Text) && Directory.Exists(TxtSourceFolder.Text)) n++;
        if (!string.IsNullOrWhiteSpace(TxtSetupFile.Text) && File.Exists(TxtSetupFile.Text)) n++;
        if (!string.IsNullOrWhiteSpace(TxtOutputFolder.Text)) n++;
        return n;
    }

    private void UpdateFlowUi()
    {
        if (_running) return;

        var pathsSet = PathsSet();
        var ready = pathsSet >= 3;
        var signingOn = ChkSignPayload.IsChecked == true;
        var signingValid = ValidateSigningInputs() is null;
        var allValid = ready && signingValid;

        TxtSubtitleEmpty.IsVisible = !ready;
        TxtSubtitleReady.IsVisible = ready;

        // Step-1 badge flips to the green check once all three paths validate.
        Step1Badge.Classes.Set("done", ready);
        Step1BadgeText.Text = ready ? "✓" : "1";

        RowSource.Classes.Set("filled", !string.IsNullOrWhiteSpace(TxtSourceFolder.Text));
        RowSetup.Classes.Set("filled", !string.IsNullOrWhiteSpace(TxtSetupFile.Text));
        RowOutput.Classes.Set("filled", !string.IsNullOrWhiteSpace(TxtOutputFolder.Text));
        RowOverwrite.Classes.Set("filled", ready);   // rows compress together once filled

        // Step 2: preview at 55% until the switch is on; indigo border while expanded.
        Card2.Classes.Set("preview", !signingOn);
        Card2.BorderBrush = signingOn ? B("AccentBorder") : B("BorderSoftBrush");
        Step2Badge.Classes.Set("active", signingOn);
        TxtSignHint.IsVisible = !signingOn;

        // Step 3 preview wakes up once everything upstream is satisfied.
        Card3.Classes.Set("preview", !allValid);
        TxtPackageHint.Text = ready && !string.IsNullOrWhiteSpace(TxtSetupFile.Text)
            ? $"→ {Path.GetFileNameWithoutExtension(TxtSetupFile.Text)}.intunewin"
            : "→ output.intunewin";

        BtnPackage.IsEnabled = allValid;
        KbdChip.IsVisible = allValid;

        if (ValidateInputs() is { } error && pathsSet > 0)
            SetStatus(error, "Error");
        else
            SetStatus(PackagingFlow.PathsStatus(pathsSet, signingOn), null);
    }

    /// <summary>Background-computed derived info: source file count + size in
    /// the step-1 header, MSI readout in the card footer, est. size in step 3.</summary>
    private async Task RefreshDerivedAsync()
    {
        var gen = ++_derivedGeneration;
        var source = TxtSourceFolder.Text;
        var setup = TxtSetupFile.Text;

        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            TxtSourceSummary.Text = "or drop a folder anywhere";
            RowMsi.IsVisible = false;
            return;
        }

        var (fileCount, totalBytes) = await Task.Run(() =>
        {
            try
            {
                int count = 0;
                long bytes = 0;
                foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    count++;
                    try { bytes += new FileInfo(f).Length; } catch { /* unreadable — skip */ }
                }
                return (count, bytes);
            }
            catch
            {
                return (0, 0L);
            }
        });
        if (gen != _derivedGeneration) return;   // a newer path superseded this walk

        TxtSourceSummary.Text = fileCount > 0
            ? PackagingFlow.SourceSummary(fileCount, totalBytes)
            : "or drop a folder anywhere";
        if (BtnPackage.IsEnabled && !string.IsNullOrWhiteSpace(setup))
            TxtPackageHint.Text =
                $"→ {Path.GetFileNameWithoutExtension(setup)}.intunewin · est. {PackagingFlow.FormatSize(totalBytes)}";

        if (!string.IsNullOrWhiteSpace(setup) && File.Exists(setup) &&
            Path.GetExtension(setup).Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            var msi = await Task.Run(() => PackageInspector.TryReadMsiInfo(setup));
            if (gen != _derivedGeneration) return;
            RowMsi.IsVisible = msi is not null;
            if (msi is not null) TxtMsiReadout.Text = PackagingFlow.MsiReadout(msi);
        }
        else
        {
            RowMsi.IsVisible = false;
        }
    }

    // ── Package run ─────────────────────────────────────────────────────────

    private async void BtnPackage_Click(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        TxtOutput.Text = string.Empty;

        if (ValidateInputs() is { } error)
        {
            AppendOutput(error);
            SetStatus(error, "Error");
            return;
        }

        SaveCurrentSettings();

        var source = TxtSourceFolder.Text!.Trim();
        var setup = TxtSetupFile.Text!.Trim();
        var output = TxtOutputFolder.Text!.Trim();
        bool signing = ChkSignPayload.IsChecked == true;
        bool isMsi = Path.GetExtension(setup).Equals(".msi", StringComparison.OrdinalIgnoreCase);

        EnterRunningMode(source, setup, output, signing, isMsi);

        // Progress<T> created on the UI thread marshals callbacks back to it.
        var progress = new Progress<string>(OnProgressLine);
        _cts = new CancellationTokenSource();

        try
        {
            // Signing runs BEFORE wrapping (in place); the app drives its stage
            // row directly since the engine's log lines aren't prefix-stable.
            if (signing)
            {
                ActivateStage(PackageStage.Sign);
                var options = BuildSigningOptions();
                var signer = PayloadSigner.TryCreate(options, out var locateError);
                if (signer is null) { FailRun(PackageStage.Sign, locateError!); return; }

                var signed = await signer.SignAsync(source, setup, options, progress, _cts.Token);
                if (!signed.Success) { FailRun(PackageStage.Sign, signed.Error!); return; }
                SetStageDone(PackageStage.Sign, PackagingFlow.SignStageDetail(CurrentCertMode(), TxtPfxPath.Text));
            }

            var request = new PackageRequest(source, setup, output, ChkOverwrite.IsChecked == true);
            var result = await _packager.PackageAsync(request, progress, _cts.Token);

            if (result.Success) CompleteRun(result.OutputPath!);
            else FailRun(_activeStage ?? PackageStage.Assemble, result.Error!);
        }
        catch (OperationCanceledException)
        {
            ExitToForm();
            SetStatus("Cancelled.", "Error");
        }
        catch (Exception ex)
        {
            // Backstop for anything the signing engine throws instead of
            // returning a failure — an exception escaping this async void
            // handler would take down the whole process.
            FailRun(_activeStage ?? PackageStage.Assemble, ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            StopRunTimers();
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        // The zip stage can't be interrupted mid-stream (BCL limitation), so a
        // cancel there lands at the next stage boundary — hence the status hint.
        BtnCancel.IsEnabled = false;
        SetStatus("cancelling…", "Error");
        if (_activeStage is { } active && _stageRows.TryGetValue(active, out var row))
            row.Root.Opacity = 0.45;
        _cts?.Cancel();
    }

    private void BtnNewPackage_Click(object? sender, RoutedEventArgs e) => ExitToForm();

    private void BtnRawLog_Click(object? sender, RoutedEventArgs e)
    {
        _rawLogVisible = !_rawLogVisible;
        StageCard.IsVisible = !_rawLogVisible;
        RawLogCard.IsVisible = _rawLogVisible;
        BtnRawLog.Content = _rawLogVisible ? "raw log ▴" : "raw log ▾";
    }

    private async void BtnOpenOutput_Click(object? sender, RoutedEventArgs e)
    {
        var dir = TxtOutputFolder.Text;
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
    }

    // ── Running mode ────────────────────────────────────────────────────────

    private void EnterRunningMode(string source, string setup, string output, bool signing, bool isMsi)
    {
        _running = true;
        _rawLogVisible = false;
        _encryptPercent = null;
        _activeStage = null;
        _activeSpinner = null;

        BuildStageRows(signing, isMsi);

        TxtRunTitle.Text = "Packaging";
        TxtBreadcrumb.Text =
            $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(source))}/ → {Path.GetFileName(setup)} → {Abbreviate(output)}";
        ChipSigned.IsVisible = signing;
        TxtChipSigned.Text = "signed · " + CurrentCertMode() switch
        {
            CertMode.Pkcs11 => "hsm",
            CertMode.TrustedSigning => "azure",
            _ => "pfx",
        };

        TxtPercent.Text = "0%";
        TxtStageName.Text = "Starting";
        TxtElapsed.Text = "00:00 elapsed";
        BarRun.Value = 0;

        PanelForm.IsVisible = false;
        PanelRunning.IsVisible = true;
        StageCard.IsVisible = true;
        RawLogCard.IsVisible = false;
        BtnNewPackage.IsVisible = false;

        BtnPackage.IsVisible = false;
        BtnOpenOutput.IsVisible = false;
        KbdChip.IsVisible = false;
        BtnCancel.IsVisible = true;
        BtnCancel.IsEnabled = true;
        BtnRawLog.IsVisible = true;
        BtnRawLog.Content = "raw log ▾";
        SetStatus("", null);

        _elapsed.Restart();
        _elapsedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick -= ElapsedTick;
        _elapsedTimer.Tick += ElapsedTick;
        _elapsedTimer.Start();

        _spinTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _spinTimer.Tick -= SpinTick;
        _spinTimer.Tick += SpinTick;
        _spinTimer.Start();
    }

    private void ElapsedTick(object? s, EventArgs e) =>
        TxtElapsed.Text = PackagingFlow.FormatElapsed(_elapsed.Elapsed) + " elapsed";

    private void SpinTick(object? s, EventArgs e)
    {
        _spinAngle = (_spinAngle + 29) % 360;
        if (_activeSpinner is not null)
            _activeSpinner.RenderTransform = new RotateTransform(_spinAngle);
    }

    private void StopRunTimers()
    {
        _elapsed.Stop();
        _elapsedTimer?.Stop();
        _spinTimer?.Stop();
    }

    private void ExitToForm()
    {
        _running = false;
        StopRunTimers();
        PanelRunning.IsVisible = false;
        PanelForm.IsVisible = true;
        BtnCancel.IsVisible = false;
        BtnRawLog.IsVisible = false;
        BtnOpenOutput.IsVisible = false;
        BtnPackage.IsVisible = true;
        UpdateFlowUi();
    }

    private void OnProgressLine(string line)
    {
        AppendOutput(line);

        if (PackagingFlow.EncryptPercent(line) is { } pct)
            _encryptPercent = pct;

        if (PackagingFlow.StageFor(line) is { } stage && _stageRows.ContainsKey(stage))
            ActivateStage(stage);

        UpdateProgressUi();
    }

    /// <summary>Marks every visible stage before <paramref name="stage"/> done
    /// and the stage itself active. Stage order matches execution order.</summary>
    private void ActivateStage(PackageStage stage)
    {
        foreach (var (s, row) in _stageRows)
        {
            if (s < stage && row.State is StageState.Pending or StageState.Active)
                SetStageState(s, StageState.Done);
        }
        if (_stageRows[stage].State == StageState.Pending)
            SetStageState(stage, StageState.Active);
        _activeStage = stage;
        TxtStageName.Text = _stageRows[stage].DisplayName;
    }

    private void SetStageDone(PackageStage stage, string? detail = null)
    {
        if (!_stageRows.TryGetValue(stage, out var row)) return;
        if (detail is not null) row.Detail.Text = detail;
        SetStageState(stage, StageState.Done);
        UpdateProgressUi();
    }

    private void CompleteRun(string outputPath)
    {
        foreach (var s in _stageRows.Keys.ToList())
            SetStageState(s, StageState.Done);
        _activeStage = null;

        BarRun.Value = 1;
        TxtPercent.Text = "Done";
        TxtStageName.Text = "Complete";
        TxtBreadcrumb.Text += $" → {Path.GetFileName(outputPath)}";

        _running = false;
        StopRunTimers();
        BtnCancel.IsVisible = false;
        BtnNewPackage.IsVisible = true;
        BtnOpenOutput.IsVisible = true;
        BtnPackage.IsVisible = true;
        BtnPackage.IsEnabled = true;
        AppendOutput("Package created successfully!");
        SetStatus($"done — {Path.GetFileName(outputPath)} created", "Success");
    }

    private void FailRun(PackageStage stage, string message)
    {
        AppendOutput("ERROR  " + message);
        if (_stageRows.TryGetValue(stage, out var row))
        {
            row.Detail.Text = message;
            SetStageState(stage, StageState.Error);
        }
        TxtStageName.Text = "Failed";

        _running = false;
        StopRunTimers();
        BtnCancel.IsVisible = false;
        BtnNewPackage.IsVisible = true;
        BtnPackage.IsVisible = true;
        BtnPackage.IsEnabled = true;
        SetStatus(message, "Error");

        // The raw log usually holds the real story — surface it.
        if (!_rawLogVisible) BtnRawLog_Click(null, new RoutedEventArgs());
    }

    private void UpdateProgressUi()
    {
        if (_stageRows.Count == 0) return;
        var completed = _stageRows.Values.Count(r => r.State == StageState.Done);
        var inStage = _activeStage == PackageStage.Encrypt ? _encryptPercent : null;
        var fraction = PackagingFlow.OverallFraction(_stageRows.Count, completed, inStage);
        BarRun.Value = fraction;
        if (TxtPercent.Text != "Done")
            TxtPercent.Text = $"{(int)(fraction * 100)}%";
    }

    // ── Stage rows ──────────────────────────────────────────────────────────

    private enum StageState { Pending, Active, Done, Error }

    private sealed class StageRow
    {
        public required Border Root { get; init; }
        public required Panel BadgeHost { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Detail { get; init; }
        public required string DisplayName { get; init; }
        public StageState State { get; set; }
    }

    private void BuildStageRows(bool signing, bool isMsi)
    {
        _stageRows.Clear();
        StageList.Children.Clear();

        void Add(PackageStage stage, string title, string detail = "")
        {
            var badgeHost = new Panel { Width = 18, Height = 18, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var titleBlock = new TextBlock
            {
                Text = title, FontSize = 12.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            var detailBlock = new TextBlock
            {
                Text = detail, FontSize = 10.5, FontFamily = (FontFamily)this.FindResource("MonoFont")!,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            Grid.SetColumn(titleBlock, 1);
            Grid.SetColumn(detailBlock, 2);
            grid.Children.Add(badgeHost);
            grid.Children.Add(titleBlock);
            grid.Children.Add(detailBlock);
            var root = new Border { Height = 40, Padding = new Thickness(16, 0), Child = grid };

            var row = new StageRow
            {
                Root = root, BadgeHost = badgeHost, Title = titleBlock, Detail = detailBlock,
                DisplayName = title, State = StageState.Pending,
            };
            _stageRows[stage] = row;
            StageList.Children.Add(root);
            ApplyStageVisual(row);
        }

        if (signing) Add(PackageStage.Sign, "Sign payload");
        Add(PackageStage.Zip, "Zip payload");
        Add(PackageStage.Encrypt, "Encrypt + integrity", "aes-256-cbc · hmac-sha256");
        if (isMsi) Add(PackageStage.MsiMetadata, "MSI metadata");
        Add(PackageStage.DetectionXml, "Detection.xml");
        Add(PackageStage.Assemble, "Assemble .intunewin");
    }

    private void SetStageState(PackageStage stage, StageState state)
    {
        if (!_stageRows.TryGetValue(stage, out var row) || row.State == state) return;
        row.State = state;
        ApplyStageVisual(row);
        if (stage == PackageStage.Zip && state == StageState.Done &&
            string.IsNullOrEmpty(row.Detail.Text) && TxtSourceSummary.Text is { } sum && sum.Contains("file"))
            row.Detail.Text = sum;
    }

    private void ApplyStageVisual(StageRow row)
    {
        row.Root.Opacity = 1;
        row.BadgeHost.Children.Clear();

        switch (row.State)
        {
            case StageState.Pending:
                row.BadgeHost.Children.Add(new Ellipse
                {
                    Width = 6, Height = 6, Fill = B("Dimmer"),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
                row.Title.Foreground = B("Dimmer");
                row.Title.FontWeight = FontWeight.Normal;
                row.Detail.Foreground = B("Dimmer");
                break;

            case StageState.Active:
                var ring = new Ellipse { Width = 16, Height = 16, Stroke = B("AccentSoft"), StrokeThickness = 2 };
                var arc = new Arc
                {
                    Width = 16, Height = 16, Stroke = B("Accent"), StrokeThickness = 2,
                    StartAngle = -90, SweepAngle = 100,
                };
                _activeSpinner = arc;
                row.BadgeHost.Children.Add(ring);
                row.BadgeHost.Children.Add(arc);
                row.Title.Foreground = B("Fg");
                row.Title.FontWeight = FontWeight.SemiBold;
                row.Detail.Foreground = B("AccentLink");
                break;

            case StageState.Done:
                if (_activeSpinner is not null && row.BadgeHost.Children.Contains(_activeSpinner))
                    _activeSpinner = null;
                row.BadgeHost.Children.Add(new Border
                {
                    Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                    Background = B("SuccessSoft"),
                    Child = new TextBlock
                    {
                        Text = "✓", FontSize = 10, Foreground = B("Success"),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                });
                row.Title.Foreground = B("Dim");
                row.Title.FontWeight = FontWeight.Normal;
                row.Detail.Foreground = B("Dimmer");
                break;

            case StageState.Error:
                row.BadgeHost.Children.Add(new TextBlock
                {
                    Text = "✕", FontSize = 12, Foreground = B("Error"),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
                row.Title.Foreground = B("Error");
                row.Title.FontWeight = FontWeight.SemiBold;
                row.Detail.Foreground = B("Error");
                break;
        }
    }

    // ── Output log / status ──────────────────────────────────────────────--

    private void AppendOutput(string line)
    {
        TxtOutput.Text += line + Environment.NewLine;
        TxtOutput.CaretIndex = TxtOutput.Text?.Length ?? 0;
    }

    private void SetStatus(string text, string? brushKey)
    {
        TxtStatus.Text = text;
        TxtStatus.Foreground = brushKey is null ? B("Dimmer") : B(brushKey);
    }

    private IBrush B(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var res) && res is IBrush brush
            ? brush
            : Brushes.Transparent;

    private static string Abbreviate(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }

    // ── Drag and drop (whole-window target) ─────────────────────────────────

    private void WireDragDrop()
    {
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = GetDropPath(e) is not null ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (GetDropPath(e) is not { } path) return;

        switch (PackagingFlow.ClassifyDrop(path, Directory.Exists(path), IsWithin(e, RowOutput)))
        {
            case DropKind.InspectPackage:
                // Inspecting is read-only, so it's fine even mid-run.
                new InspectWindow(path).Show(this);
                break;
            case DropKind.OutputFolder when !_running:
                TxtOutputFolder.Text = path;
                break;
            case DropKind.SourceFolder when !_running:
                ApplySourceFolder(path);
                break;
            case DropKind.SetupFile when !_running:
                TxtSetupFile.Text = path;
                break;
        }
        e.Handled = true;
    }

    private static bool IsWithin(DragEventArgs e, Visual target) =>
        e.Source is Visual v && (v == target || v.GetVisualAncestors().Contains(target));

    private static string? GetDropPath(DragEventArgs e)
    {
        var files = e.Data.GetFiles()?.ToArray();
        return files is { Length: 1 } ? files[0].Path.LocalPath : null;
    }

    // ── macOS menu bar ───────────────────────────────────────────────────────
    // NativeMenuItem.Click is EventHandler-shaped, so thin wrappers adapt to the
    // existing button handlers.

    private void MenuBrowseSource(object? sender, EventArgs e) => BtnBrowseSource_Click(sender, new RoutedEventArgs());
    private void MenuBrowseSetup(object? sender, EventArgs e) => BtnBrowseSetup_Click(sender, new RoutedEventArgs());
    private void MenuBrowseOutput(object? sender, EventArgs e) => BtnBrowseOutput_Click(sender, new RoutedEventArgs());
    private void MenuOpenOutput(object? sender, EventArgs e) => BtnOpenOutput_Click(sender, new RoutedEventArgs());

    private void MenuPackage(object? sender, EventArgs e)
    {
        // The button disables itself during a run; the menu must honor that too.
        if (!_running && BtnPackage.IsEnabled) BtnPackage_Click(sender, new RoutedEventArgs());
    }

    private async void MenuInspectPackage(object? sender, EventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select a .intunewin package to inspect",
                AllowMultiple = false,
                SuggestedStartLocation = await StartLocationAsync(
                    Directory.Exists(TxtOutputFolder.Text) ? TxtOutputFolder.Text : null),
                FileTypeFilter =
                [
                    new FilePickerFileType("Intune packages") { Patterns = ["*.intunewin"] },
                    new FilePickerFileType("All files") { Patterns = ["*"] },
                ],
            });
            if (files.Count > 0)
                new InspectWindow(files[0].Path.LocalPath).Show(this);
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't open the inspector — " + ex.Message, "Error");
        }
    }

    private TextBox? FocusedTextBox() => FocusManager?.GetFocusedElement() as TextBox;
    private void OnEditCut(object? sender, EventArgs e) => FocusedTextBox()?.Cut();
    private void OnEditCopy(object? sender, EventArgs e) => FocusedTextBox()?.Copy();
    private void OnEditPaste(object? sender, EventArgs e) => FocusedTextBox()?.Paste();
    private void OnEditSelectAll(object? sender, EventArgs e) => FocusedTextBox()?.SelectAll();

    private void OnWindowMinimize(object? sender, EventArgs e) => WindowState = WindowState.Minimized;
    private void OnWindowZoom(object? sender, EventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnHelpGitHub(object? sender, EventArgs e) => OpenUrl("https://github.com/thefinder808/WrapTune-MacOS");
    private void OnHelpIssue(object? sender, EventArgs e) => OpenUrl("https://github.com/thefinder808/WrapTune-MacOS/issues/new");
    private void OnHelpReleases(object? sender, EventArgs e) => OpenUrl("https://github.com/thefinder808/WrapTune-MacOS/releases");

    private void OpenUrl(string url)
    {
        try { _ = Launcher.LaunchUriAsync(new Uri(url)); } catch { /* best-effort */ }
    }

    private Window? _aboutWindow;
    private async void OnHelpAbout(object? sender, EventArgs e)
    {
        if (_aboutWindow is not null) { _aboutWindow.Activate(); return; }

        var version = await UpdateService.CurrentVersionAsync() ?? "development build";
        var link = new Button { Content = "github.com/thefinder808/WrapTune-MacOS", Background = Brushes.Transparent, Padding = new Thickness(0) };
        link.Click += (_, _) => OpenUrl("https://github.com/thefinder808/WrapTune-MacOS");
        _aboutWindow = new Window
        {
            Title = "About WrapTune",
            Width = 360, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "WrapTune", FontSize = 18, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = $"Version {version}", FontSize = 12.5 },
                    new TextBlock { Text = "Builds Microsoft Intune .intunewin packages on macOS.", FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
                    link,
                },
            },
        };
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show(this);
    }

    // ── Updates ──────────────────────────────────────────────────────────────

    /// <summary>Fire-and-forget on-launch check, throttled to once a day. Errors are
    /// never surfaced — background only.</summary>
    private void StartLaunchUpdateCheck()
    {
        var s = AppSettings.Load();
        if (!UpdateService.ShouldAutoCheck(s.LastUpdateCheckUtc, DateTime.UtcNow)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await _updates.CheckAsync(CancellationToken.None);
                if (r.Error is not null) return;   // failed check → don't stamp; retry next launch

                // Stamp the check time only on a successful check. Update (not
                // Load+Save) so this background write can't clobber a UI-thread
                // save that lands at the same moment.
                var latest = AppSettings.Update(x => x.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o"));

                if (r.UpdateAvailable && r.Info is not null && r.Info.Version != latest.SkippedUpdateVersion)
                    Dispatcher.UIThread.Post(() => new UpdateWindow(r.Info, _updates).Show(this));
            }
            catch { /* background only */ }
        });
    }

    /// <summary>Help → "Check for Updates…": always gives feedback — the update dialog
    /// when one is available, otherwise the result in the status bar + log.</summary>
    private async void OnCheckForUpdates(object? sender, EventArgs e)
    {
        SetStatus("Checking for updates…", null);
        var r = await _updates.CheckAsync(CancellationToken.None);

        if (r.UpdateAvailable && r.Info is not null)
        {
            // A manual check overrides a previously skipped version on purpose —
            // the user is explicitly asking.
            new UpdateWindow(r.Info, _updates).Show(this);
            SetStatus($"Update {r.Info.Version} available.", null);
        }
        else if (r.Error is not null)
        {
            AppendOutput("Update check: " + r.Error);
            SetStatus("Couldn't check for updates — " + r.Error, "Error");
        }
        else
        {
            SetStatus("You're up to date.", "Success");
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────--

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
