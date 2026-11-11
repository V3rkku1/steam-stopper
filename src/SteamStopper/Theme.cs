using System.Windows;
using System.Windows.Media;
using SteamStopper.Core;

namespace SteamStopper;

/// <summary>
/// Colour themes. The XAML uses these brushes through DynamicResource, so swapping them in the
/// application resources repaints every page at once, with no restart. Every theme but the built-in
/// one is a small CSS file of custom properties (see <see cref="CssTheme"/>); users can write their own.
/// </summary>
public static class Theme
{
    public static readonly Dictionary<string, string> Names = new()
    {
        ["default"] = "Steam Multitool", ["steam"] = "Steam", ["liquid"] = "Liquid Glass", ["game"] = "From my game"
    };

    // Brush keys in App.xaml, and the CSS property for each: AccentText is --accent-text, HeroStart is --hero.
    private static readonly string[] Keys =
        ["Bg", "Sidebar", "Card", "Raised", "Hover", "Pressed", "Line", "Popup", "Selected", "Accent", "AccentText",
         "Text", "Muted", "Faint", "Green", "Red", "Amber", "HeroStart", "HeroLine"];

    private static string Var(string key) => key == "HeroStart" ? "hero" : System.Text.RegularExpressions.Regex.Replace(key, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();

    // Steam's own client colours: navy panels, Steam blue for actions.
    private const string SteamCss = """
        :root {
          --bg: #171A21; --sidebar: #171D25; --card: #1B2838; --raised: #233B52; --hover: #2A475E;
          --pressed: #3D6C8D; --line: #26394D; --popup: #1F2F42; --selected: #2A475E;
          --accent: #1A9FFF; --accent-text: #66C0F4; --text: #DCDEDF; --muted: #8F98A0; --faint: #5D6B78;
          --green: #90BA3C; --red: #E05A4F; --amber: #E4B54A; --hero: #213A55; --hero-line: #2A475E;
        }
        """;

    // iOS-style Liquid Glass: translucent panels over Windows 11's blurred backdrop, hairline light
    // borders, iOS system colours and rounder cards. Without the backdrop (Windows 10) it falls back to solid.
    private const string LiquidCss = """
        :root {
          --backdrop: acrylic;
          --radius: 22;
          --bg: rgba(16, 18, 24, 0.55); --sidebar: rgba(255, 255, 255, 0.04);
          --card: rgba(255, 255, 255, 0.07); --raised: rgba(255, 255, 255, 0.10);
          --hover: rgba(255, 255, 255, 0.14); --pressed: rgba(255, 255, 255, 0.20);
          --line: rgba(255, 255, 255, 0.14); --popup: rgba(34, 36, 44, 0.96);
          --selected: rgba(10, 132, 255, 0.28);
          --accent: #0A84FF; --accent-text: #64D2FF;
          --text: #FFFFFF; --muted: rgba(235, 235, 245, 0.62); --faint: rgba(235, 235, 245, 0.32);
          --green: #30D158; --red: #FF453A; --amber: #FFD60A;
          --hero: rgba(10, 132, 255, 0.30); --hero-line: rgba(255, 255, 255, 0.18);
        }
        """;

    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamStopper", "Themes");

    /// <summary>The window backdrop the theme asked for: none, acrylic or mica.</summary>
    public static string Backdrop { get; private set; } = "none";

    private static Dictionary<string, Color>? _defaults;
    // The theme's own colours as last applied, before any solid fallback; what New theme copies.
    private static Dictionary<string, Color> _applied = [];

    /// <param name="name">default, steam, liquid, game, or css:file.css from <see cref="Folder"/>.</param>
    /// <param name="accent">For "game": the colour taken from the game's cover; the rest stays the default dark.</param>
    public static void Apply(string name, Color? accent = null)
    {
        var resources = Application.Current.Resources;
        // The colours App.xaml starts with, kept so switching back needs no table of its own.
        var defaults = _defaults ??= Keys.ToDictionary(k => k, k => Of(resources, k));
        var card = defaults["Card"];
        var vars = CssTheme.Parse(name switch
        {
            "steam" => SteamCss,
            "liquid" => LiquidCss,
            _ when name.StartsWith("css:") => Read(name[4..]),
            _ => ""
        });
        var tint = name == "game" && accent is { } a ? new Dictionary<string, Color>
        {
            ["Accent"] = a, ["AccentText"] = Mix(a, Colors.White, 0.35), ["Selected"] = Mix(a, card, 0.78),
            ["HeroStart"] = Mix(a, card, 0.72), ["HeroLine"] = Mix(a, card, 0.6)
        } : [];
        Color Pick(string key) =>
            vars.TryGetValue(Var(key), out var css) && CssTheme.Color(css) is { } v ? Color.FromArgb(v.A, v.R, v.G, v.B)
            : tint.TryGetValue(key, out var c) ? c : defaults[key];
        _applied = Keys.ToDictionary(k => k, Pick);
        foreach (var key in Keys.Where(k => k != "HeroStart"))
            resources[key] = Frozen(new SolidColorBrush(Pick(key)));
        resources["HeroStart"] = Frozen(new LinearGradientBrush(
            [new GradientStop(Pick("HeroStart"), 0), new GradientStop(Pick("Card"), 0.65)], new Point(0, 0), new Point(1, 1)));
        resources["CardRadius"] = new CornerRadius(vars.TryGetValue("radius", out var r) && double.TryParse(r.Replace("px", ""), System.Globalization.CultureInfo.InvariantCulture, out var px) ? Math.Clamp(px, 0, 40) : 16);
        Backdrop = vars.GetValueOrDefault("backdrop", "none").ToLowerInvariant();
    }

    /// <summary>When the backdrop isn't available (Windows 10, older 11): make the see-through background solid.</summary>
    public static void Opaque()
    {
        var resources = Application.Current.Resources;
        foreach (var key in new[] { "Bg", "Sidebar" })
            if (resources[key] is SolidColorBrush { Color.A: < 255 } b)
                resources[key] = Frozen(new SolidColorBrush(Mix(Color.FromRgb(16, 18, 24), Color.FromRgb(b.Color.R, b.Color.G, b.Color.B), b.Color.A / 255.0)));
    }

    /// <summary>The CSS file names in the themes folder.</summary>
    public static List<string> Files() =>
        System.IO.Directory.Exists(Folder) ? System.IO.Directory.GetFiles(Folder, "*.css").Select(System.IO.Path.GetFileName).OfType<string>().Order().ToList() : [];

    private static string Read(string file)
    {
        try { return System.IO.File.ReadAllText(System.IO.Path.Combine(Folder, System.IO.Path.GetFileName(file))); }
        catch { return ""; } // deleted or being saved: keep the built-in colours
    }

    /// <summary>A starting point for a new theme: every property with the colour it has right now.</summary>
    public static string Template()
    {
        var resources = Application.Current.Resources;
        var lines = Keys.Select(k => (Key: k, C: _applied.TryGetValue(k, out var c) ? c : Of(resources, k)))
            .Select(p => $"  --{Var(p.Key)}: {(p.C.A == 255 ? $"#{p.C.R:X2}{p.C.G:X2}{p.C.B:X2}" : $"rgba({p.C.R}, {p.C.G}, {p.C.B}, {p.C.A / 255.0:0.##})")};");
        return "/* Steam Multitool theme. Save this file and the app updates right away.\n"
               + "   Colours: #rgb, #rrggbb, #rrggbbaa, rgb() or rgba().\n"
               + "   --backdrop: acrylic or mica makes the window see-through on Windows 11 (use see-through --bg).\n"
               + "   --radius: card corner radius in pixels. */\n"
               + ":root {\n  --backdrop: " + Backdrop + ";\n  --radius: " + ((CornerRadius)resources["CardRadius"]).TopLeft + ";\n"
               + string.Join("\n", lines) + "\n}\n";
    }

    private static Color Mix(Color a, Color b, double towardB) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * towardB), (byte)(a.G + (b.G - a.G) * towardB), (byte)(a.B + (b.B - a.B) * towardB));

    /// <summary>
    /// The cover's most eye-catching colour: pixels grouped by hue, weighted by how bright and saturated
    /// they are, then nudged bright enough to read as an accent on the dark theme. Null for a grey cover.
    /// </summary>
    public static Color? AccentOf(string imagePath)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(imagePath);
            image.DecodePixelWidth = 48;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            var bins = new double[36, 4]; // weight, r, g, b per 10 degrees of hue
            for (var i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
                if (max < 0.25 || chroma < 0.15) continue; // dark or grey
                var hue = max == r ? (g - b) / chroma % 6 : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
                var bin = (int)((hue * 60 + 360) % 360 / 10);
                var weight = chroma / max * max;
                bins[bin, 0] += weight;
                bins[bin, 1] += r * weight;
                bins[bin, 2] += g * weight;
                bins[bin, 3] += b * weight;
            }
            var best = Enumerable.Range(0, 36).MaxBy(k => bins[k, 0] + 0.5 * (bins[(k + 35) % 36, 0] + bins[(k + 1) % 36, 0]));
            if (bins[best, 0] < pixels.Length / 4 * 0.02) return null;
            double R = bins[best, 1] / bins[best, 0], G = bins[best, 2] / bins[best, 0], B = bins[best, 3] / bins[best, 0];
            // Scale so the brightest channel is about 0.85: vivid on the dark cards, still readable with white text.
            var scale = 0.85 / Math.Max(R, Math.Max(G, B));
            return Color.FromRgb((byte)Math.Min(255, R * scale * 255), (byte)Math.Min(255, G * scale * 255), (byte)Math.Min(255, B * scale * 255));
        }
        catch { return null; } // unreadable or missing image
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    private static Color Of(ResourceDictionary resources, string key) => resources[key] switch
    {
        SolidColorBrush b => b.Color,
        LinearGradientBrush g => g.GradientStops[0].Color,
        _ => Colors.Transparent
    };
}
