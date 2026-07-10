using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Headless runtime checks: the .axaml actually parses, the theme dictionaries
/// resolve, and every x:Named control the code-behind touches exists. Catches
/// the runtime XAML failures a compile can't.
/// </summary>
public sealed class SmokeTests
{
    [AvaloniaFact]
    public void MainWindow_loads_with_all_named_controls()
    {
        var w = new MainWindow();
        w.Show();

        Assert.NotNull(w.FindControl<TextBox>("TxtSourceFolder"));
        Assert.NotNull(w.FindControl<TextBox>("TxtSetupFile"));
        Assert.NotNull(w.FindControl<TextBox>("TxtOutputFolder"));
        Assert.NotNull(w.FindControl<TextBox>("TxtOutput"));
        Assert.NotNull(w.FindControl<CheckBox>("ChkOverwrite"));
        Assert.NotNull(w.FindControl<Button>("BtnPackage"));
        Assert.NotNull(w.FindControl<Button>("BtnTheme"));

        var open = w.FindControl<Button>("BtnOpenOutput");
        Assert.NotNull(open);
        Assert.False(open!.IsVisible);   // hidden until a package succeeds

        var cancel = w.FindControl<Button>("BtnCancel");
        Assert.NotNull(cancel);
        Assert.False(cancel!.IsVisible); // shown only while a package run is in flight
    }

    [AvaloniaFact]
    public void Theme_dictionaries_resolve_every_token()
    {
        var w = new MainWindow();
        w.Show();

        // Theme-dictionary resources are variant-scoped — resolve with the
        // active variant (exactly how the code-behind's SetStatus looks them up).
        foreach (var key in new[]
                 {
                     "WindowBg", "PanelBg", "Panel2Bg", "LogBg", "BorderBrush", "BorderSoftBrush",
                     "Fg", "Dim", "Dimmer", "Accent", "AccentSoft", "AccentOn", "Success", "Error",
                 })
            Assert.True(w.TryFindResource(key, w.ActualThemeVariant, out _), $"Missing theme resource: {key}");
    }

    [AvaloniaFact]
    public void Theme_initializes_consistently()
    {
        var w = new MainWindow();
        w.Show();
        var toggle = w.FindControl<Button>("BtnTheme")!;

        // The toggle label offers the OTHER theme; it must match the active variant.
        if (Equals(Application.Current!.RequestedThemeVariant, ThemeVariant.Dark))
            Assert.Equal("◑ Light", toggle.Content);
        else
            Assert.Equal("◐ Dark", toggle.Content);
    }
}
