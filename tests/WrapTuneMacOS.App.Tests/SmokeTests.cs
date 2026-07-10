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

        // Step 1 — files card
        Assert.NotNull(w.FindControl<TextBox>("TxtSourceFolder"));
        Assert.NotNull(w.FindControl<TextBox>("TxtSetupFile"));
        Assert.NotNull(w.FindControl<TextBox>("TxtOutputFolder"));
        Assert.NotNull(w.FindControl<ToggleSwitch>("ChkOverwrite"));
        Assert.NotNull(w.FindControl<Border>("Step1Badge"));

        // Step 2 — signing
        Assert.NotNull(w.FindControl<ToggleSwitch>("ChkSignPayload"));
        var signBody = w.FindControl<StackPanel>("SignBody");
        Assert.NotNull(signBody);
        Assert.NotNull(w.FindControl<RadioButton>("RbPfx"));
        Assert.NotNull(w.FindControl<RadioButton>("RbPkcs11"));
        Assert.NotNull(w.FindControl<RadioButton>("RbTrustedSigning"));

        // Running view + footer
        Assert.NotNull(w.FindControl<TextBox>("TxtOutput"));          // raw log
        Assert.NotNull(w.FindControl<StackPanel>("StageList"));
        Assert.NotNull(w.FindControl<TextBlock>("TxtPercent"));
        Assert.NotNull(w.FindControl<TextBlock>("TxtStatus"));
        Assert.NotNull(w.FindControl<Button>("BtnPackage"));

        var running = w.FindControl<StackPanel>("PanelRunning");
        Assert.NotNull(running);
        Assert.False(running!.IsVisible);   // form state first

        var cancel = w.FindControl<Button>("BtnCancel");
        Assert.NotNull(cancel);
        Assert.False(cancel!.IsVisible);    // shown only while a package run is in flight

        var open = w.FindControl<Button>("BtnOpenOutput");
        Assert.NotNull(open);
        Assert.False(open!.IsVisible);      // hidden until a package succeeds
    }

    [AvaloniaFact]
    public void Theme_dictionaries_resolve_every_token()
    {
        var w = new MainWindow();
        w.Show();

        // Theme-dictionary resources are variant-scoped — resolve with the
        // active variant (exactly how the code-behind's B() looks them up).
        foreach (var key in new[]
                 {
                     "WindowBg", "PanelBg", "Panel2Bg", "LogBg", "BorderBrush", "BorderSoftBrush",
                     "HairlineSoft", "ChipBorder", "Fg", "Dim", "Dimmer",
                     "Accent", "AccentSoft", "AccentOn", "AccentText", "AccentLink", "AccentBorder",
                     "Success", "SuccessSoft", "Error",
                 })
            Assert.True(w.TryFindResource(key, w.ActualThemeVariant, out _), $"Missing theme resource: {key}");
    }

    [AvaloniaFact]
    public void Theme_variant_matches_the_saved_setting()
    {
        var w = new MainWindow();
        w.Show();

        // The window applies the persisted theme on load (menu Window → Toggle
        // Theme flips it); whatever settings said must be what's active.
        var expectDark = AppSettings.Load().Theme == "Midnight";
        Assert.Equal(expectDark ? ThemeVariant.Dark : ThemeVariant.Light,
            Application.Current!.RequestedThemeVariant);
    }
}
