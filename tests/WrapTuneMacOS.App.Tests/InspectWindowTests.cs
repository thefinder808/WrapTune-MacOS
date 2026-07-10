using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WrapTuneMacOS.Packaging;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Headless checks for the Inspect Package window: the XAML parses and every
/// x:Named control the code-behind touches exists. The inspection logic itself
/// is covered in Packaging.Tests (PackageInspectorTests) — shared core.
/// </summary>
public sealed class InspectWindowTests
{
    private static string BuildSamplePackage(string root)
    {
        var src = Path.Combine(root, "source");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(src, "setup.ps1"), "Write-Host hi");

        var result = new IntuneWinWriter().PackageAsync(new PackageRequest(
                src, Path.Combine(src, "setup.ps1"), outDir, Overwrite: true))
            .GetAwaiter().GetResult();
        Assert.True(result.Success, result.Error);
        return result.OutputPath!;
    }

    [AvaloniaFact]
    public void InspectWindow_loads_with_all_named_controls()
    {
        var root = Path.Combine(Path.GetTempPath(), "wraptune-inspect-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var package = BuildSamplePackage(root);
            var w = new InspectWindow(package);
            w.Show();

            Assert.NotNull(w.FindControl<TextBlock>("TxtPkgPath"));
            Assert.NotNull(w.FindControl<TextBlock>("TxtName"));
            Assert.NotNull(w.FindControl<TextBlock>("TxtSetup"));
            Assert.NotNull(w.FindControl<TextBlock>("TxtToolVersion"));
            Assert.NotNull(w.FindControl<TextBlock>("TxtSizes"));
            Assert.NotNull(w.FindControl<TextBlock>("TxtMsi"));
            Assert.NotNull(w.FindControl<TextBlock>("TxtChecks"));
            Assert.NotNull(w.FindControl<TextBlock>("TxtVerdict"));
            Assert.NotNull(w.FindControl<ListBox>("LstEntries"));
            Assert.NotNull(w.FindControl<TextBlock>("TxtInspectStatus"));

            var extract = w.FindControl<Button>("BtnExtract");
            Assert.NotNull(extract);
            Assert.False(extract!.IsEnabled);   // enabled only after a successful inspection
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
