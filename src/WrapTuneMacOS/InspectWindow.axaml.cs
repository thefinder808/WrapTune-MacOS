using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WrapTuneMacOS.Packaging;

namespace WrapTuneMacOS;

/// <summary>
/// The Inspect Package window: Detection.xml metadata, the HMAC/digest/size
/// verification verdicts, and the payload's contents — the same checks the
/// verification ladder runs, surfaced in the UI (recognition over recall).
/// Inspection runs on <see cref="PackageInspector"/>, the shared core the CLI
/// `inspect` command uses too.
/// </summary>
public partial class InspectWindow : Window
{
    private readonly string _packagePath;
    private PackageInspection? _inspection;

    // Parameterless ctor for the Avalonia XAML loader/previewer only.
    public InspectWindow() : this("") { }

    public InspectWindow(string packagePath)
    {
        _packagePath = packagePath;
        InitializeComponent();
        TxtPkgPath.Text = packagePath;
        Opened += (_, _) => _ = RunInspectionAsync();
    }

    private async Task RunInspectionAsync()
    {
        if (string.IsNullOrEmpty(_packagePath)) return;   // XAML-previewer instance
        try
        {
            var i = await Task.Run(() => PackageInspector.Inspect(_packagePath));
            _inspection = i;

            var d = i.Detection;
            TxtName.Text = d.Name;
            TxtSetup.Text = d.SetupFile;
            TxtToolVersion.Text = d.ToolVersion;
            TxtSizes.Text = $"{d.UnencryptedContentSize:N0} B payload  ·  {i.EncryptedSizeBytes:N0} B encrypted";
            TxtMsi.Text = d.MsiInfo is { } msi
                ? $"{msi.MsiProductCode}  v{msi.MsiProductVersion}  ({ContextName(msi.MsiExecutionContext)})"
                : "—";

            TxtChecks.Text =
                $"HMAC {(i.MacValid ? "OK" : "FAIL")}   ·   " +
                $"Digest {(i.DigestValid ? "OK" : "FAIL")}   ·   " +
                $"Size {(i.SizeValid ? "OK" : "FAIL")}";
            TxtVerdict.Text = i.IsValid
                ? "✓ Valid — format and crypto check out"
                : "✗ Invalid — this package would be rejected";
            if (this.TryFindResource(i.IsValid ? "Success" : "Error", ActualThemeVariant, out var res)
                && res is IBrush brush)
                TxtVerdict.Foreground = brush;

            LstEntries.ItemsSource = i.PayloadEntries;
            TxtInspectStatus.Text = $"Payload: {i.PayloadEntryCount} entr{(i.PayloadEntryCount == 1 ? "y" : "ies")}";
            BtnExtract.IsEnabled = i.MacValid;   // never offer to extract unauthenticated bytes
        }
        catch (Exception ex)
        {
            TxtChecks.Text = "Could not inspect this file.";
            TxtVerdict.Text = ex.Message;
            TxtInspectStatus.Text = "Not a readable .intunewin package.";
        }
    }

    private static string ContextName(int executionContext) => executionContext switch
    {
        0 => "per-machine",
        1 => "per-user",
        2 => "dual-purpose",
        _ => $"context {executionContext}",
    };

    private async void OnExtract(object? sender, RoutedEventArgs e)
    {
        try
        {
            var suggested = Path.GetFileNameWithoutExtension(_packagePath) + "-payload.zip";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save decrypted payload",
                SuggestedFileName = suggested,
                DefaultExtension = "zip",
            });
            if (file is null) return;

            BtnExtract.IsEnabled = false;
            TxtInspectStatus.Text = "Extracting…";
            var dest = file.Path.LocalPath;
            var r = await Task.Run(() => PackageInspector.ExtractPayloadZip(_packagePath, dest));
            TxtInspectStatus.Text = r.IsValid
                ? $"Payload written to {dest}"
                : "Extracted, but digest/size did not match Detection.xml — treat with suspicion.";
        }
        catch (Exception ex)
        {
            TxtInspectStatus.Text = "Extract failed: " + ex.Message;
        }
        finally
        {
            BtnExtract.IsEnabled = _inspection?.MacValid == true;
        }
    }
}
