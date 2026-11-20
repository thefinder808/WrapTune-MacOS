using Avalonia;
using Avalonia.Automation;
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
    private int? _zipPercent;
    private string? _lastOutputPath;   // the package the last successful run produced
    private IStorageFile? _lastOutputFile;   // …resolved up front: a drag-out must start synchronously
    private Point? _receiptPress;      // where a press on the receipt began (drag threshold)
    private bool _draggingOut;         // our own drag is in flight — don't treat it as a drop on us
    private DropKind? _dropTarget;     // what the drag currently over the window would do

    // ── batch queue (not persisted) ──
    private readonly List<QueueItem> _queue = [];
    private bool _queueDone;           // the run view is showing a finished batch's results
    private bool InQueue => _queue.Count > 0;
    internal IReadOnlyList<QueueItem> Queue => _queue;
    private string? _sizeHint;         // "est. 39.8 MB" once the source folder has been walked
    private bool _themeChosen;         // false = follow the macOS appearance
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
        WirePathDisplays();
        WireSigningRefresh();
        UpdateSigningUi();
        UpdateFlowUi();
        Opened += (_, _) => StartLaunchUpdateCheck();
        // Following the system appearance: brushes set from code must be
        // re-resolved when macOS flips between light and dark.
        ActualThemeVariantChanged += (_, _) => { if (PanelForm is not null) UpdateFlowUi(); };
    }

    // ── Settings ────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        var s = AppSettings.Load();
        if (!string.IsNullOrEmpty(s.SourceFolder)) TxtSourceFolder.Text = s.SourceFolder;
        if (!string.IsNullOrEmpty(s.SetupFile)) TxtSetupFile.Text = s.SetupFile;
        if (!string.IsNullOrEmpty(s.OutputFolder)) TxtOutputFolder.Text = s.OutputFolder;
        _theme = string.IsNullOrEmpty(s.Theme) ? "Daylight" : s.Theme;
        // Settings written before ThemeChosen existed: "Midnight" can only have
        // been picked by hand (Daylight was the default), so keep honoring it.
        _themeChosen = s.ThemeChosen || _theme == "Midnight";
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
            s.ThemeChosen = _themeChosen;
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

    /// <summary>Applies the named theme when the user has picked one; until
    /// then the app follows the macOS appearance.</summary>
    private void ApplyTheme(string name)
    {
        var dark = name == "Midnight";
        if (Application.Current is { } app)
            app.RequestedThemeVariant = !_themeChosen ? ThemeVariant.Default
                : dark ? ThemeVariant.Dark : ThemeVariant.Light;
        _theme = dark ? "Midnight" : "Daylight";
        if (PanelForm is not null) UpdateFlowUi();   // re-resolve brushes set from code
    }

    private void OnToggleTheme(object? sender, EventArgs e)
    {
        // Flip what's on screen now, which may be the system's choice.
        _themeChosen = true;
        ApplyTheme(ActualThemeVariant == ThemeVariant.Dark ? "Daylight" : "Midnight");
        SaveCurrentSettings();
    }

    private void OnUseSystemTheme(object? sender, EventArgs e)
    {
        _themeChosen = false;
        ApplyTheme(_theme);
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
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the source folder — or several, to queue them",
            AllowMultiple = true,
            SuggestedStartLocation = await StartLocationAsync(TxtSourceFolder.Text),
        });
        var folders = picked.Select(f => f.Path.LocalPath).ToList();
        if (folders.Count > 1 || (folders.Count == 1 && InQueue)) AddToQueue(folders);
        else if (folders.Count == 1) ApplySourceFolder(folders[0]);
    }

    // ── Batch queue ─────────────────────────────────────────────────────────

    /// <summary>Queues <paramref name="folders"/>. A single folder with no
    /// queue showing is just the ordinary source folder.</summary>
    internal void AddToQueue(IEnumerable<string> folders)
    {
        var added = BatchQueue.Plan(folders, _queue);
        if (added.Count == 0) return;
        if (!InQueue && added.Count == 1)
        {
            ApplySourceFolder(added[0].Folder);
            return;
        }

        _queue.AddRange(added);
        if (string.IsNullOrWhiteSpace(TxtOutputFolder.Text) &&
            PackagingFlow.DefaultOutputFolder(_queue[0].Folder) is { } parent)
            TxtOutputFolder.Text = parent;
        RenderQueue();
    }

    private void BtnClearQueue_Click(object? sender, RoutedEventArgs e)
    {
        _queue.Clear();
        RenderQueue();
    }

    /// <summary>Rebuilds the queue rows and swaps the step-1 card between its
    /// single-package rows and the queue.</summary>
    private void RenderQueue()
    {
        var queued = InQueue;
        QueueList.IsVisible = queued;
        BtnClearQueue.IsVisible = queued;
        TxtStep1Title.Text = queued ? "Queue" : "Files";
        RowSource.IsVisible = !queued;
        RowSetup.IsVisible = !queued;
        RowOutputName.IsVisible = !queued;

        QueueList.Children.Clear();
        foreach (var item in _queue)
            QueueList.Children.Add(BuildQueueRow(item));

        if (queued)
        {
            TxtSourceSummary.Text = BatchQueue.Summary(_queue.Count);
            RowMsi.IsVisible = false;
            _sizeHint = null;
            UpdateFlowUi();
        }
        else
        {
            OnPathsChanged();   // back to one package: recompute its summary
        }
    }

    private Border BuildQueueRow(QueueItem item)
    {
        var problem = BatchQueue.Problem(item);

        var name = new TextBlock { Text = item.FolderName, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Classes.Add("fieldLabel");

        var setup = new TextBlock
        {
            Text = problem ?? PackagingFlow.SetupDisplay(item.Folder, item.SetupFile, null),
            IsVisible = true,
            Margin = new Thickness(12, 0),
        };
        setup.Classes.Add("pathDisplay");
        if (problem is not null) setup.Classes.Add("queueProblem");

        var choose = new Button { Content = "choose" };
        choose.Classes.Add("browse");
        AutomationProperties.SetName(choose, $"Choose the setup file for {item.FolderName}");
        choose.Click += async (_, _) => await ChooseQueueSetupAsync(item, choose);

        var remove = new Button { Content = "×", Margin = new Thickness(10, 0, 0, 0), FontSize = 15 };
        remove.Classes.Add("link");
        remove.Classes.Add("mono");
        AutomationProperties.SetName(remove, $"Remove {item.FolderName} from the queue");
        remove.Click += (_, _) =>
        {
            _queue.Remove(item);
            RenderQueue();
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("170,*,Auto,Auto"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetColumn(setup, 1);
        Grid.SetColumn(choose, 2);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(name);
        grid.Children.Add(setup);
        grid.Children.Add(choose);
        grid.Children.Add(remove);

        var row = new Border { Child = grid };
        row.Classes.Add("fieldRow");
        row.Classes.Add("filled");
        ToolTip.SetTip(row, $"{item.Folder}\n→ {item.OutputName}.intunewin");
        return row;
    }

    /// <summary>Sets a queued folder's setup file and refreshes its row.</summary>
    internal void SetQueueSetup(QueueItem item, string path)
    {
        item.SetupFile = path;
        RenderQueue();
    }

    private async Task ChooseQueueSetupAsync(QueueItem item, Control anchor)
    {
        void Pick(string path) => SetQueueSetup(item, path);

        async Task BrowseAsync()
        {
            if (await PickSetupFileAsync(item.Folder) is { } path) Pick(path);
        }

        var found = await Task.Run(() => PackagingFlow.FindInstallers(item.Folder));
        if (found.RelativePaths.Count == 0) await BrowseAsync();
        else BuildSetupMenu(item.Folder, found, item.SetupFile, Pick, BrowseAsync).ShowAt(anchor);
    }

    private string? ValidateQueueInputs()
    {
        foreach (var item in _queue)
            if (BatchQueue.Problem(item) is { } problem)
                return $"{item.FolderName}: {problem}";
        if (string.IsNullOrWhiteSpace(TxtOutputFolder.Text))
            return "Output folder is not specified.";
        return ValidateSigningInputs();
    }

    private async void BtnBrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select the output folder for the .intunewin file", TxtOutputFolder.Text) is { } folder)
            TxtOutputFolder.Text = folder;
    }

    /// <summary>The setup file has to live inside the source folder, so offer
    /// the installers that are actually in it. The file dialog is still there
    /// ("Browse…") and is what opens when there's no source folder to list.</summary>
    private async void BtnBrowseSetup_Click(object? sender, RoutedEventArgs e)
    {
        var source = TxtSourceFolder.Text?.Trim();
        var found = await Task.Run(() => PackagingFlow.FindInstallers(source));
        if (found.RelativePaths.Count == 0)
        {
            await BrowseSetupFileAsync();
            return;
        }
        BuildSetupMenu(source!, found).ShowAt(BtnBrowseSetup);
    }

    /// <summary>The picker for the single-package form's setup file.</summary>
    internal MenuFlyout BuildSetupMenu(string source, PackagingFlow.InstallerList found) =>
        BuildSetupMenu(source, found, TxtSetupFile.Text, path => TxtSetupFile.Text = path, BrowseSetupFileAsync);

    /// <summary>The installers in <paramref name="source"/> as a menu; the same
    /// menu serves the form's setup row and each queue row.</summary>
    internal MenuFlyout BuildSetupMenu(string source, PackagingFlow.InstallerList found,
        string? currentChoice, Action<string> pick, Func<Task> browseElsewhere)
    {
        var mono = (FontFamily)this.FindResource("MonoFont")!;
        var current = currentChoice?.Trim() ?? "";
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        foreach (var relative in found.RelativePaths)
        {
            var full = Path.Combine(source, relative);
            var item = new MenuItem
            {
                // A TextBlock, not a string: a string header treats "_" as an
                // access-key marker and would eat underscores in file names.
                Header = new TextBlock { Text = relative, FontFamily = mono, FontSize = 12 },
                Icon = string.Equals(full, current, StringComparison.OrdinalIgnoreCase)
                    ? new TextBlock { Text = "✓", FontSize = 12 }
                    : null,
            };
            AutomationProperties.SetName(item, relative);
            item.Click += (_, _) => pick(full);
            menu.Items.Add(item);
        }

        if (found.Omitted > 0)
            menu.Items.Add(new MenuItem
            {
                Header = new TextBlock { Text = $"{found.Omitted} more — use Browse…", FontSize = 12 },
                IsEnabled = false,
            });

        menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = new TextBlock { Text = "Browse…", FontSize = 12.5 } };
        AutomationProperties.SetName(browse, "Browse for another file");
        browse.Click += async (_, _) => await browseElsewhere();
        menu.Items.Add(browse);
        return menu;
    }

    private async Task BrowseSetupFileAsync()
    {
        if (await PickSetupFileAsync(TxtSourceFolder.Text) is { } path) TxtSetupFile.Text = path;
    }

    private async Task<string?> PickSetupFileAsync(string? startFolder)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the setup file",
            AllowMultiple = false,
            SuggestedStartLocation = await StartLocationAsync(
                Directory.Exists(startFolder) ? startFolder : null),
            FileTypeFilter =
            [
                new FilePickerFileType("Installers")
                {
                    Patterns = InstallerExtensions.All.Select(x => "*" + x).ToArray(),
                },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
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
        TxtSetupFile.Text = PackagingFlow.GuessSetupFile(folder) ?? string.Empty;
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
        if (InQueue) return ValidateQueueInputs();
        if (string.IsNullOrWhiteSpace(TxtSourceFolder.Text) || !Directory.Exists(TxtSourceFolder.Text))
            return "Source folder is missing or does not exist.";
        if (string.IsNullOrWhiteSpace(TxtSetupFile.Text) || !File.Exists(TxtSetupFile.Text))
            return "Setup file is missing or does not exist.";
        if (!IntuneWinWriter.IsInsideFolder(TxtSourceFolder.Text, TxtSetupFile.Text))
            return IntuneWinWriter.SetupOutsideSourceError;
        if (string.IsNullOrWhiteSpace(TxtOutputFolder.Text))
            return "Output folder is not specified.";
        if (IntuneWinWriter.OutputNameError(TxtOutputName.Text) is { } nameError)
            return nameError;
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
        TxtSourceFolder.TextChanged += (_, _) => { RefreshOutputName(); OnPathsChanged(); };
        TxtSetupFile.TextChanged += (_, _) => { RefreshOutputName(); OnPathsChanged(); };
        TxtOutputFolder.TextChanged += (_, _) => OnPathsChanged();
        TxtOutputName.TextChanged += (_, _) => OnPathsChanged();
        RefreshOutputName();   // paths restored from settings were set before this was wired
    }

    /// <summary>Re-derives the output name whenever the source or setup file
    /// changes. Deliberately replaces a hand-typed name: one left over from the
    /// previous package is exactly how two packages end up overwriting each other.</summary>
    private void RefreshOutputName() =>
        TxtOutputName.Text = PackagingFlow.DefaultOutputName(TxtSourceFolder.Text, TxtSetupFile.Text);

    // ── Path rows ───────────────────────────────────────────────────────────

    /// <summary>Each path box has a read-only stand-in laid over it. While the
    /// box isn't focused the stand-in shows a shortened path and the box goes
    /// transparent (still clickable, still the value everything else reads);
    /// focusing the box brings the full editable path back.</summary>
    private void WirePathDisplays()
    {
        foreach (var box in new[] { TxtSourceFolder, TxtSetupFile, TxtOutputFolder })
        {
            box.GotFocus += (_, _) => RefreshPathDisplays();
            box.LostFocus += (_, _) => RefreshPathDisplays();
            box.TextChanged += (_, _) => RefreshPathDisplays();
        }
        RefreshPathDisplays();
    }

    private void RefreshPathDisplays()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ShowPath(TxtSourceFolder, TxtSourceDisplay, PackagingFlow.AbbreviateHome(TxtSourceFolder.Text, home));
        ShowPath(TxtSetupFile, TxtSetupDisplay,
            PackagingFlow.SetupDisplay(TxtSourceFolder.Text, TxtSetupFile.Text, home));
        ShowPath(TxtOutputFolder, TxtOutputDisplay, PackagingFlow.AbbreviateHome(TxtOutputFolder.Text, home));
    }

    private static void ShowPath(TextBox box, TextBlock display, string shortened)
    {
        var standIn = !box.IsFocused && shortened.Length > 0;
        display.Text = shortened;
        display.IsVisible = standIn;
        box.Opacity = standIn ? 0 : 1;
        // The stand-in may be trimmed or relative; the full path is a hover away.
        ToolTip.SetTip(box, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text);
    }

    /// <summary>Every input <see cref="ValidateSigningInputs"/> reads refreshes the
    /// footer as it changes; no debounce — these checks are cheap and local,
    /// unlike the path fields' folder-walk.</summary>
    private void WireSigningRefresh()
    {
        TextBox[] validated =
        [
            TxtPfxPath, TxtSecret,
            TxtPkcs11Module,
            TxtTsEndpoint, TxtTsAccount, TxtTsProfile, TxtTsToken,
        ];
        foreach (var box in validated)
            box.TextChanged += (_, _) => UpdateFlowUi();
        ChkSignAllFiles.IsCheckedChanged += (_, _) => UpdateFlowUi();
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
        var signingOn = ChkSignPayload.IsChecked == true;
        var allValid = ValidateInputs() is null;
        // "Ready" is about the files step only: signing has its own card.
        var ready = InQueue
            ? _queue.All(i => BatchQueue.Problem(i) is null) && !string.IsNullOrWhiteSpace(TxtOutputFolder.Text)
            : pathsSet >= 3;

        TxtSubtitleEmpty.IsVisible = !ready;
        TxtSubtitleReady.IsVisible = ready;

        // Step-1 badge flips to the green check once all three paths validate.
        Step1Badge.Classes.Set("done", ready);
        Step1BadgeText.Text = ready ? "✓" : "1";

        RowSource.Classes.Set("filled", !string.IsNullOrWhiteSpace(TxtSourceFolder.Text));
        RowSetup.Classes.Set("filled", !string.IsNullOrWhiteSpace(TxtSetupFile.Text));
        RowOutput.Classes.Set("filled", !string.IsNullOrWhiteSpace(TxtOutputFolder.Text));
        RowOutputName.Classes.Set("filled", !string.IsNullOrWhiteSpace(TxtOutputName.Text));
        RowOverwrite.Classes.Set("filled", ready);   // rows compress together once filled
        DropZone.IsVisible = !InQueue && string.IsNullOrWhiteSpace(TxtSourceFolder.Text);

        // Step 2: preview at 55% until the switch is on; indigo border while expanded.
        Card2.Classes.Set("preview", !signingOn);
        Card2.BorderBrush = signingOn ? B("AccentBorder") : B("BorderSoftBrush");
        Step2Badge.Classes.Set("active", signingOn);
        TxtSignHint.IsVisible = !signingOn;

        // The size estimate sits beside the action. (The output's name has its
        // own row now, so repeating it here only squeezed the status text.)
        TxtPackageHint.IsVisible = allValid && _sizeHint is not null;
        TxtPackageHint.Text = _sizeHint;

        BtnPackage.IsEnabled = allValid;
        KbdChip.IsVisible = allValid;

        if (InQueue)
        {
            if (ValidateInputs() is { } queueError) SetStatus(queueError, "Error");
            else SetStatus(BatchQueue.ReadyStatus(_queue.Count, signingOn), null);
        }
        else if (ValidateInputs() is { } error && pathsSet > 0)
            SetStatus(error, "Error");
        else
            SetStatus(PackagingFlow.PathsStatus(pathsSet, signingOn), null);
    }

    /// <summary>Background-computed derived info: source file count + size in
    /// the step-1 header, MSI readout in the card footer, est. size in step 3.</summary>
    private async Task RefreshDerivedAsync()
    {
        var gen = ++_derivedGeneration;
        if (InQueue) return;   // the header shows the queue's own summary
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
                    if (IntuneWinWriter.IsMacMetadataFile(f)) continue;   // the engine leaves these out
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
        _sizeHint = fileCount > 0 ? $"est. {PackagingFlow.FormatSize(totalBytes)}" : null;
        if (!_running) UpdateFlowUi();

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

        if (InQueue)
        {
            await RunQueueAsync();
            return;
        }

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
            var options = signing ? BuildSigningOptions() : null;   // reads controls — must stay on the UI thread
            var (outputPath, runError, failedStage) = await SignAndPackageAsync(
                source, setup, output, NullIfBlank(TxtOutputName.Text), options,
                ChkOverwrite.IsChecked == true, progress, _cts.Token);

            if (outputPath is not null) CompleteRun(outputPath);
            else FailRun(failedStage, runError!);
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

    /// <summary>One package, start to finish: sign the payload if asked, then
    /// wrap it. Shared by the single run and each item of a batch. Returns the
    /// package's path, or the error and the stage it happened in.</summary>
    private async Task<(string? OutputPath, string? Error, PackageStage FailedStage)> SignAndPackageAsync(
        string source, string setup, string output, string? outputName, SigningOptions? signing,
        bool overwrite, IProgress<string> progress, CancellationToken ct)
    {
        // Signing runs BEFORE wrapping (in place); the app drives its stage
        // row directly since the engine's log lines aren't prefix-stable.
        if (signing is not null)
        {
            ActivateStage(PackageStage.Sign);

            // Task.Run for the same reason PackageAsync uses it internally:
            // the in-process signing engine does synchronous crypto and
            // network work (Azure endpoint, TSA) before/without yielding,
            // which beachballs the UI thread if called from it.
            var signed = await Task.Run(async () =>
            {
                var signer = PayloadSigner.TryCreate(signing, out var locateError);
                if (signer is null) return SignResult.Fail(locateError!);
                return await signer.SignAsync(source, setup, signing, progress, ct);
            });

            if (!signed.Success) return (null, signed.Error!, PackageStage.Sign);
            SetStageDone(PackageStage.Sign, PackagingFlow.SignStageDetail(CurrentCertMode(), TxtPfxPath.Text));
        }

        var result = await _packager.PackageAsync(
            new PackageRequest(source, setup, output, overwrite, outputName), progress, ct);
        return result.Success
            ? (result.OutputPath, null, PackageStage.Assemble)
            : (null, result.Error!, _activeStage ?? PackageStage.Assemble);
    }

    /// <summary>Packages every queued folder in turn, with the same output
    /// folder, overwrite and signing settings. One failing doesn't stop the
    /// rest; Cancel stops the current one and leaves the remainder not run.</summary>
    private async Task RunQueueAsync()
    {
        var items = _queue.ToList();
        var output = TxtOutputFolder.Text!.Trim();
        bool signing = ChkSignPayload.IsChecked == true;
        bool overwrite = ChkOverwrite.IsChecked == true;
        var options = signing ? BuildSigningOptions() : null;
        var progress = new Progress<string>(OnProgressLine);
        var total = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<QueueResult>();
        _cts = new CancellationTokenSource();

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (_cts.IsCancellationRequested)
                {
                    results.Add(new QueueResult(item, null, null));   // never ran
                    continue;
                }

                var setup = item.SetupFile!;
                bool isMsi = Path.GetExtension(setup).Equals(".msi", StringComparison.OrdinalIgnoreCase);
                EnterRunningMode(item.Folder, setup, output, signing, isMsi);
                TxtRunTitle.Text = $"Packaging {i + 1} of {items.Count}";
                AppendOutput($"── {i + 1} of {items.Count} · {item.FolderName} ──");

                try
                {
                    var (outputPath, error, _) = await SignAndPackageAsync(
                        item.Folder, setup, output, item.OutputName, options, overwrite, progress, _cts.Token);
                    results.Add(new QueueResult(item, outputPath, error));
                    if (error is not null) AppendOutput("ERROR  " + error);
                }
                catch (OperationCanceledException)
                {
                    results.Add(new QueueResult(item, null, "Cancelled."));
                }
                catch (Exception ex)
                {
                    // Same backstop as the single run: nothing may escape and
                    // take the process (and the rest of the batch) down.
                    results.Add(new QueueResult(item, null, ex.Message));
                    AppendOutput("ERROR  " + ex.Message);
                }

                // Progress<T> posts its lines; let this item's stragglers land
                // before the next item rebuilds the stage rows they refer to.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            CompleteQueue(results, output, total.Elapsed);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            StopRunTimers();
        }
    }

    /// <summary>The batch's done screen: a line per folder — the package it
    /// became and its size, or why it didn't.</summary>
    private void CompleteQueue(IReadOnlyList<QueueResult> results, string output, TimeSpan elapsed)
    {
        _activeStage = null;
        _running = false;
        _queueDone = true;
        _rawLogVisible = false;
        StopRunTimers();

        TxtRunTitle.Text = BatchQueue.ResultTitle(results);
        TxtPercent.Text = "Done";
        BreadcrumbCard.IsVisible = false;
        RunHero.IsVisible = false;
        ReceiptCard.IsVisible = false;
        StageCard.IsVisible = false;
        RawLogCard.IsVisible = false;
        BtnRawLog.Content = "raw log ▾";

        QueueResultList.Children.Clear();
        var where = new TextBlock
        {
            // Time first: the path is the part that can be long enough to trim.
            Text = $"{PackagingFlow.FormatElapsed(elapsed)} total · saved to {Abbreviate(output)}",
            Margin = new Thickness(16, 8, 16, 10),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(where, output);
        where.Classes.Add("monoHint");
        QueueResultList.Children.Add(where);
        foreach (var result in results)
            QueueResultList.Children.Add(BuildQueueResultRow(result));
        QueueResultCard.IsVisible = true;

        // Reveal points at the last package built (Finder opens the folder with
        // it selected); with none built there is nothing to reveal.
        _lastOutputPath = results.LastOrDefault(r => r.Success)?.OutputPath;
        _lastOutputFile = null;

        BtnCancel.IsVisible = false;
        BtnNewPackage.IsVisible = true;
        BtnOpenOutput.IsVisible = _lastOutputPath is not null;
        BtnPackage.IsVisible = false;

        var allGood = results.All(r => r.Success);
        AppendOutput(BatchQueue.ResultStatus(results));
        SetStatus(BatchQueue.ResultStatus(results), allGood ? "Success" : "Error");
    }

    private Border BuildQueueResultRow(QueueResult result)
    {
        var badge = new Border
        {
            Child = new TextBlock { Text = result.Success ? "✓" : result.Skipped ? "–" : "✕" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        badge.Classes.Add("badge");
        if (result.Success) badge.Classes.Add("done");
        else if (!result.Skipped) badge.Classes.Add("failed");

        var name = new TextBlock
        {
            Text = result.Success ? Path.GetFileName(result.OutputPath) : result.Item.FolderName,
            FontFamily = (FontFamily)this.FindResource("MonoFont")!,
            FontSize = 12,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        string detailText;
        if (result.Success)
        {
            long size = 0;
            try { size = new FileInfo(result.OutputPath!).Length; } catch { /* shown as 0 B */ }
            detailText = PackagingFlow.FormatSize(size);
        }
        else
        {
            detailText = result.Skipped ? "not run" : result.Error!;
        }
        var detail = new TextBlock
        {
            Text = detailText,
            MaxWidth = 300,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        detail.Classes.Add("monoHint");
        if (result.Error is not null) detail.Classes.Add("queueProblem");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(badge);
        grid.Children.Add(name);
        grid.Children.Add(detail);

        var row = new Border { Height = 38, Padding = new Thickness(16, 0), Child = grid };
        // The full story is a hover away: where it went, or the whole error.
        ToolTip.SetTip(row, result.Success ? result.OutputPath : result.Skipped ? "Not run — the batch was cancelled." : result.Error);
        return row;
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
        // After a batch the results list has the stage card's place; the log
        // opens beneath it instead of swapping with it.
        StageCard.IsVisible = !_rawLogVisible && !_queueDone;
        RawLogCard.IsVisible = _rawLogVisible;
        BtnRawLog.Content = _rawLogVisible ? "raw log ▴" : "raw log ▾";
    }

    /// <summary>Selects the package just built in Finder. Falls back to opening
    /// the output folder when there's no package to point at (the File menu item
    /// before a run, or the file has since been moved).</summary>
    private async void BtnOpenOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (OperatingSystem.IsMacOS() && _lastOutputPath is { } package && File.Exists(package))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(package);
                System.Diagnostics.Process.Start(psi)?.Dispose();
                return;
            }
            catch
            {
                // Fall through to opening the folder.
            }
        }

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
        _zipPercent = null;
        _activeStage = null;
        _activeSpinner = null;

        BuildStageRows(signing, isMsi);

        TxtRunTitle.Text = "Packaging";
        BreadcrumbCard.IsVisible = true;
        RunHero.IsVisible = true;
        ReceiptCard.IsVisible = false;
        QueueResultCard.IsVisible = false;
        _queueDone = false;
        TxtPercent.Foreground = B("Fg");
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
        TxtPackageHint.IsVisible = false;
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
        if (PackagingFlow.ZipPercent(line) is { } zipPct)
            _zipPercent = zipPct;

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
        TxtRunTitle.Text = "Packaged";
        // File name first: the strip trims from the end, and the name is the
        // one thing here that must never be cut off.
        _lastOutputPath = outputPath;
        TxtBreadcrumb.Text =
            $"{Path.GetFileName(outputPath)}  ·  in {Abbreviate(Path.GetDirectoryName(outputPath) ?? "")}";
        ShowReceipt(outputPath);

        _running = false;
        StopRunTimers();
        BtnCancel.IsVisible = false;
        BtnNewPackage.IsVisible = true;
        BtnOpenOutput.IsVisible = true;
        // No Package button here: after success it would invite an accidental
        // re-run; "← new package" returns to the (still-filled) form instead.
        BtnPackage.IsVisible = false;
        AppendOutput("Package created successfully!");
        SetStatus($"done — {Path.GetFileName(outputPath)} created", "Success");
    }

    /// <summary>Swaps the strip and progress hero for the receipt card.</summary>
    private void ShowReceipt(string outputPath)
    {
        long size = 0;
        try { size = new FileInfo(outputPath).Length; } catch { /* shown as 0 B rather than failing a finished run */ }

        TxtReceiptName.Text = Path.GetFileName(outputPath);
        TxtReceiptSummary.Text = PackagingFlow.ReceiptSummary(
            size, _elapsed.Elapsed, ChipSigned.IsVisible ? TxtChipSigned.Text : null);
        TxtReceiptLocation.Text = Abbreviate(Path.GetDirectoryName(outputPath) ?? "");
        TxtReceiptSetup.Text = TxtSetupDisplay.Text;
        RowReceiptMsi.IsVisible = RowMsi.IsVisible;
        TxtReceiptMsi.Text = TxtMsiReadout.Text;
        ToolTip.SetTip(TxtReceiptLocation, outputPath);

        BreadcrumbCard.IsVisible = false;
        RunHero.IsVisible = false;
        ReceiptCard.IsVisible = true;

        // Resolve the file now. A drag has to begin inside the pointer event
        // that triggers it; awaiting a lookup there would be too late.
        _lastOutputFile = null;
        _ = ResolveOutputFileAsync(outputPath);
    }

    private async Task ResolveOutputFileAsync(string outputPath)
    {
        try
        {
            var file = await StorageProvider.TryGetFileFromPathAsync(outputPath);
            if (_lastOutputPath == outputPath) _lastOutputFile = file;
        }
        catch
        {
            // No drag-out for this package; Reveal and Copy path still work.
        }
    }

    private async void BtnCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastOutputPath is null || Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(_lastOutputPath);
            SetStatus("path copied", "Success");
        }
        catch
        {
            SetStatus("couldn't reach the clipboard", "Error");
        }
    }

    // Dragging the receipt's file row drags the package itself, so it can be
    // dropped on the Intune admin center's upload box (or anywhere else).
    private void ReceiptFile_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            _receiptPress = e.GetPosition(this);
    }

    private void ReceiptFile_PointerReleased(object? sender, PointerReleasedEventArgs e) => _receiptPress = null;

    private async void ReceiptFile_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_receiptPress is not { } start || _lastOutputFile is not { } file) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - start.X) < 4 && Math.Abs(now.Y - start.Y) < 4) return;   // a click, not a drag

        _receiptPress = null;
        _draggingOut = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(file));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
        }
        catch
        {
            // A refused drag isn't worth an error; the file is where the receipt says.
        }
        finally
        {
            _draggingOut = false;
        }
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
        TxtRunTitle.Text = "Packaging failed";
        // A frozen "16%" in the normal color reads as "still going".
        TxtPercent.Text = "Failed";
        TxtPercent.Foreground = B("Error");

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
        var inStage = _activeStage switch
        {
            PackageStage.Zip => _zipPercent,
            PackageStage.Encrypt => _encryptPercent,
            _ => null,
        };
        var fraction = PackagingFlow.OverallFraction(_stageRows.Count, completed, inStage);
        BarRun.Value = fraction;
        if (TxtPercent.Text is not ("Done" or "Failed"))
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

    private static string Abbreviate(string path) =>
        PackagingFlow.AbbreviateHome(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // ── Drag and drop (whole-window target) ─────────────────────────────────

    private void WireDragDrop()
    {
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ShowDropTarget(null));
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        var kind = ClassifyDrag(e, out _);
        e.DragEffects = kind is not null ? DragDropEffects.Link : DragDropEffects.None;
        ShowDropTarget(kind);
        e.Handled = true;
    }

    /// <summary>Shows where a drag would land before it's dropped: the row it
    /// would fill lights up and the footer says what letting go will do. Null
    /// clears it. Mid-run only "inspect" is possible, so nothing else is offered.</summary>
    internal void ShowDropTarget(DropKind? kind)
    {
        if (_running && kind != DropKind.InspectPackage) kind = null;
        if (kind == _dropTarget) return;
        _dropTarget = kind;

        RowSource.Classes.Set("dropTarget", kind == DropKind.SourceFolder);
        RowSetup.Classes.Set("dropTarget", kind == DropKind.SetupFile);
        RowOutput.Classes.Set("dropTarget", kind == DropKind.OutputFolder);
        DropZone.Classes.Set("dropTarget",
            kind is DropKind.SourceFolder or DropKind.InspectPackage or DropKind.Queue);

        if (_running) return;   // the footer belongs to the run
        if (kind is { } k) SetStatus(PackagingFlow.DropHint(k), "AccentLink");
        else UpdateFlowUi();    // put the normal status back
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        ShowDropTarget(null);
        if (ClassifyDrag(e, out var paths) is not { } kind) return;
        var path = paths[0];

        switch (kind)
        {
            case DropKind.Queue when !_running:
                AddToQueue(paths);
                break;
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

    /// <summary>What dropping the dragged items here would do (null = nothing),
    /// and the items themselves.</summary>
    private DropKind? ClassifyDrag(DragEventArgs e, out List<string> paths)
    {
        // DataTransfer is the Avalonia 12 API (Data is obsolete on 11.3).
        paths = _draggingOut
            ? []
            : e.DataTransfer.TryGetFiles()?.Select(f => f.Path.LocalPath).ToList() ?? [];
        return PackagingFlow.ClassifyDrops(
            paths.Select(p => (p, Directory.Exists(p))).ToList(), IsWithin(e, RowOutput), InQueue);
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
