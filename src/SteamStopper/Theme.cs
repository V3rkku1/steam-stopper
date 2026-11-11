using System.Windows;
using System.Windows.Media;

namespace SteamStopper;

/// <summary>
/// Colour themes. The XAML uses these brushes through DynamicResource, so swapping them in the
/// application resources repaints every page at once, with no restart.
/// </summary>
public static class Theme
{
    public static readonly Dictionary<string, string> Names = new() { ["default"] = "Steam Stopper", ["steam"] = "Steam" };

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

    public static void Apply(string name)
    {
        var resources = Application.Current.Resources;
        // The colours App.xaml starts with, kept so switching back needs no table of its own.
        _defaults ??= Steam.Keys.ToDictionary(k => k, k => Of(resources, k));
        Color Pick(string key) => name == "steam" ? (Color)ColorConverter.ConvertFromString(Steam[key]) : _defaults[key];
        foreach (var key in Steam.Keys.Where(k => k != "HeroStart"))
            resources[key] = Frozen(new SolidColorBrush(Pick(key)));
        resources["HeroStart"] = Frozen(new LinearGradientBrush(
            [new GradientStop(Pick("HeroStart"), 0), new GradientStop(Pick("Card"), 0.65)], new Point(0, 0), new Point(1, 1)));
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
