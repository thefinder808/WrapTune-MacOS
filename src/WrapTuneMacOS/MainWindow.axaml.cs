using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using WrapTuneMacOS.Packaging;

namespace WrapTuneMacOS;

public partial class MainWindow : Window
{
    private readonly IIntuneWinPackager _packager = new IntuneWinWriter();
    private string _theme = "Daylight";
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        ApplyTheme(_theme);
        WireDragDrop();
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
    }

    private void SaveCurrentSettings() => new AppSettings
    {
        SourceFolder = TxtSourceFolder.Text,
        SetupFile = TxtSetupFile.Text,
        OutputFolder = TxtOutputFolder.Text,
        Theme = _theme,
        Overwrite = ChkOverwrite.IsChecked == true,
    }.Save();

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

    // ── Package ───────────────────────────────────────────────────────────--

    private string? ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(TxtSourceFolder.Text) || !Directory.Exists(TxtSourceFolder.Text))
            return "Source folder is missing or does not exist.";
        if (string.IsNullOrWhiteSpace(TxtSetupFile.Text) || !File.Exists(TxtSetupFile.Text))
            return "Setup file is missing or does not exist.";
        if (string.IsNullOrWhiteSpace(TxtOutputFolder.Text))
            return "Output folder is not specified.";
        return null;
    }

    private async void BtnPackage_Click(object? sender, RoutedEventArgs e)
    {
        if (ValidateInputs() is { } error)
        {
            SetStatus(error, "Error");
            return;
        }

        SaveCurrentSettings();

        TxtOutput.Text = string.Empty;
        BtnOpenOutput.IsVisible = false;
        BtnPackage.IsEnabled = false;
        SetStatus("Packaging…", "Accent");

        var request = new PackageRequest(
            TxtSourceFolder.Text!.Trim(),
            TxtSetupFile.Text!.Trim(),
            TxtOutputFolder.Text!.Trim(),
            ChkOverwrite.IsChecked == true);

        // Progress<T> created on the UI thread marshals callbacks back to it.
        var progress = new Progress<string>(AppendOutput);
        _cts = new CancellationTokenSource();

        PackageResult result;
        try
        {
            result = await _packager.PackageAsync(request, progress, _cts.Token);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            BtnPackage.IsEnabled = true;
        }

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
