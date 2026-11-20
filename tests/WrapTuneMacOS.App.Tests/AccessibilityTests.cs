using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using WrapTuneMacOS.Services;

namespace WrapTuneMacOS.UiTests;

/// <summary>
/// Guards for the things a sighted mouse user never notices going missing:
/// text contrast, names for VoiceOver, and a visible keyboard focus.
/// </summary>
public sealed class AccessibilityTests
{
    // ── Contrast ────────────────────────────────────────────────────────────

    /// <summary>WCAG 2.x AA for normal-size text. Everything this app prints in
    /// these colors is 10.5–13px, so the large-text allowance (3:1) never applies.</summary>
    private const double MinimumContrast = 4.5;

    private static readonly string[] TextColors =
        ["Fg", "Dim", "Dimmer", "AccentText", "AccentLink", "Success", "Error"];

    private static readonly string[] Backgrounds = ["WindowBg", "PanelBg", "Panel2Bg"];

    public static TheoryData<string, string, string> TextOnBackground()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var theme in new[] { "Light", "Dark" })
            foreach (var text in TextColors)
                foreach (var background in Backgrounds)
                    data.Add(theme, text, background);
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(TextOnBackground))]
    public void Text_colors_meet_AA_contrast_on_every_surface(string theme, string text, string background)
    {
        var ratio = Contrast(Color(theme, text), Color(theme, background));
        Assert.True(ratio >= MinimumContrast,
            $"{theme}: {text} on {background} is {ratio:0.00}:1 — needs {MinimumContrast}:1.");
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Button_and_placeholder_text_meet_AA_contrast(string theme)
    {
        // The primary button's label on its accent fill.
        Assert.True(Contrast(Color(theme, "AccentOn"), Color(theme, "Accent")) >= MinimumContrast);

        // Placeholders ("none selected", "password") carry the only description
        // an empty field has, so they are held to the same bar as other text.
        foreach (var key in new[]
                 {
                     "TextControlPlaceholderForeground",
                     "TextControlPlaceholderForegroundPointerOver",
                     "TextControlPlaceholderForegroundFocused",
                 })
            foreach (var background in Backgrounds)
                Assert.True(Contrast(Color(theme, key), Color(theme, background)) >= MinimumContrast,
                    $"{theme}: {key} on {background}");
    }

    private static Color Color(string theme, string key)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value), $"Missing resource {key}");
        return value switch
        {
            ISolidColorBrush brush => brush.Color,
            Color color => color,
            _ => throw new InvalidOperationException($"{key} is not a color"),
        };
    }

    /// <summary>WCAG contrast ratio, compositing a translucent foreground first.</summary>
    private static double Contrast(Color foreground, Color background)
    {
        static double Channel(double c)
        {
            c /= 255;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(double r, double g, double b) =>
            0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);

        var a = foreground.A / 255.0;
        var fg = Luminance(
            a * foreground.R + (1 - a) * background.R,
            a * foreground.G + (1 - a) * background.G,
            a * foreground.B + (1 - a) * background.B);
        var bg = Luminance(background.R, background.G, background.B);
        return (Math.Max(fg, bg) + 0.05) / (Math.Min(fg, bg) + 0.05);
    }

    // ── Names ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Every_interactive_control_has_a_name_a_screen_reader_can_speak()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "wraptune-a11y-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        AppSettings.BaseDirOverride = sandbox;
        try
        {
            Window[] windows =
            [
                new MainWindow(),
                new InspectWindow(""),
                new UpdateWindow(new UpdateInfo("9.9.9", "notes", "", "", ""), new UpdateService()),
            ];

            foreach (var window in windows)
            {
                window.Show();
                var unnamed = window.GetLogicalDescendants().OfType<Control>()
                    .Where(c => c is TextBox or Button or ToggleSwitch or RadioButton or RangeBase or ListBox)
                    .Where(c => string.IsNullOrWhiteSpace(SpokenName(c)))
                    .Select(c => $"{c.GetType().Name} '{c.Name}'")
                    .ToList();

                Assert.True(unnamed.Count == 0,
                    $"{window.GetType().Name} has controls with no accessible name: {string.Join(", ", unnamed)}");
                window.Close();
            }
        }
        finally
        {
            AppSettings.BaseDirOverride = null;
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>What assistive tech announces: an explicit name, else the
    /// control's own text content (a button's label).</summary>
    private static string? SpokenName(Control control) =>
        AutomationProperties.GetName(control) is { Length: > 0 } name
            ? name
            : (control as ContentControl)?.Content as string;

    [AvaloniaFact]
    public void Path_stand_ins_are_hidden_from_screen_readers()
    {
        // The (transparent) text box underneath is the real, named, editable
        // control; reading the shortened copy as well would say each path twice.
        var w = new MainWindow();
        w.Show();
        foreach (var name in new[] { "TxtSourceDisplay", "TxtSetupDisplay", "TxtOutputDisplay" })
            Assert.Equal(AccessibilityView.Raw,
                AutomationProperties.GetAccessibilityView(w.FindControl<TextBlock>(name)!));
        w.Close();
    }

    // ── Focus ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Keyboard_focus_stays_visible_on_restyled_controls()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "wraptune-a11y-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        AppSettings.BaseDirOverride = sandbox;
        try
        {
            var w = new MainWindow();
            w.Show();
            w.FindControl<ToggleSwitch>("ChkSignPayload")!.IsChecked = true;
            Pump();

            // Controls whose template or chrome the app replaces still need the
            // theme's focus ring, which a restyle can silently lose. Tab onto
            // each and check a ring is actually drawn around it.
            foreach (var name in new[]
                     {
                         "RbPfx", "RbPkcs11", "RbTrustedSigning",          // custom template
                         "BtnBrowseSource", "BtnBrowsePfx", "BtnAdvancedSigning",
                         "ChkOverwrite",
                     })
            {
                var control = w.FindControl<Control>(name)!;
                control.Focus(NavigationMethod.Tab);
                Pump();
                var layer = AdornerLayer.GetAdornerLayer(control);
                // Some themes ring an inner part (the switch's track) rather than
                // the control itself, so accept either.
                Assert.True(
                    layer is not null && layer.Children.Any(a =>
                        AdornerLayer.GetAdornedElement(a) is { } adorned &&
                        (adorned == control || control.IsVisualAncestorOf(adorned))),
                    $"{name} shows no focus ring when reached by keyboard.");
            }

            // Path box: borderless by design, so focus shows as a tinted well.
            var box = w.FindControl<TextBox>("TxtSourceFolder")!;
            var boxBorder = box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
            box.Focus(NavigationMethod.Tab);
            Pump();
            var well = Assert.IsAssignableFrom<ISolidColorBrush>(boxBorder.Background);
            Assert.True(well.Color.A > 0, "A focused path box must not look identical to an unfocused one.");
            w.Close();
        }
        finally
        {
            AppSettings.BaseDirOverride = null;
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }
}
