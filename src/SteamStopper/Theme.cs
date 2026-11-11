using System.Windows;
using System.Windows.Media;

namespace SteamStopper;

/// <summary>
/// Colour themes. The XAML uses these brushes through DynamicResource, so swapping them in the
/// application resources repaints every page at once, with no restart.
/// </summary>
public static class Theme
{
    public static readonly Dictionary<string, string> Names = new() { ["default"] = "Steam Multitool", ["steam"] = "Steam", ["game"] = "From my game" };

    // Steam's own client colours: navy panels, Steam blue for actions.
    private static readonly Dictionary<string, string> Steam = new()
    {
        ["Bg"] = "#171A21", ["Sidebar"] = "#171D25", ["Card"] = "#1B2838", ["Raised"] = "#233B52", ["Hover"] = "#2A475E",
        ["Pressed"] = "#3D6C8D", ["Line"] = "#26394D", ["Popup"] = "#1F2F42", ["Selected"] = "#2A475E",
        ["Accent"] = "#1A9FFF", ["AccentText"] = "#66C0F4", ["Text"] = "#DCDEDF", ["Muted"] = "#8F98A0", ["Faint"] = "#5D6B78",
        ["Green"] = "#90BA3C", ["Red"] = "#E05A4F", ["Amber"] = "#E4B54A",
        ["HeroStart"] = "#213A55", ["HeroLine"] = "#2A475E"
    };

    private static Dictionary<string, Color>? _defaults;

    /// <param name="accent">For "game": the colour taken from the game's cover; the rest stays the default dark.</param>
    public static void Apply(string name, Color? accent = null)
    {
        var resources = Application.Current.Resources;
        // The colours App.xaml starts with, kept so switching back needs no table of its own.
        var defaults = _defaults ??= Steam.Keys.ToDictionary(k => k, k => Of(resources, k));
        var card = defaults["Card"];
        var tint = name == "game" && accent is { } a ? new Dictionary<string, Color>
        {
            ["Accent"] = a, ["AccentText"] = Mix(a, Colors.White, 0.35), ["Selected"] = Mix(a, card, 0.78),
            ["HeroStart"] = Mix(a, card, 0.72), ["HeroLine"] = Mix(a, card, 0.6)
        } : [];
        Color Pick(string key) => name == "steam" ? (Color)ColorConverter.ConvertFromString(Steam[key])
            : tint.TryGetValue(key, out var c) ? c : defaults[key];
        foreach (var key in Steam.Keys.Where(k => k != "HeroStart"))
            resources[key] = Frozen(new SolidColorBrush(Pick(key)));
        resources["HeroStart"] = Frozen(new LinearGradientBrush(
            [new GradientStop(Pick("HeroStart"), 0), new GradientStop(Pick("Card"), 0.65)], new Point(0, 0), new Point(1, 1)));
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
