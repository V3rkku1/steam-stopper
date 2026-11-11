using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

/// <summary>
/// Themes as a small CSS file: custom properties (--bg: #171a21;) anywhere in it, other CSS ignored.
/// Colours take #rgb, #rrggbb, #rrggbbaa, rgb(), rgba() and transparent, like a browser.
/// </summary>
public static class CssTheme
{
    public static Dictionary<string, string> Parse(string css)
    {
        var text = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Matches(text, @"--([a-zA-Z0-9-]+)\s*:\s*([^;}]+)")
            .GroupBy(m => m.Groups[1].Value.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last().Groups[2].Value.Trim()); // later wins, like CSS
    }

    /// <summary>(A, R, G, B), or null when it isn't a colour this understands.</summary>
    public static (byte A, byte R, byte G, byte B)? Color(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v == "transparent") return (0, 0, 0, 0);
        var hex = Regex.Match(v, @"^#([0-9a-f]{3,8})$");
        if (hex.Success)
        {
            var h = hex.Groups[1].Value;
            if (h.Length is 3 or 4) h = string.Concat(h.Select(c => $"{c}{c}"));
            if (h.Length is not (6 or 8)) return null;
            byte At(int i) => byte.Parse(h.Substring(i, 2), NumberStyles.HexNumber);
            return (h.Length == 8 ? At(6) : (byte)255, At(0), At(2), At(4)); // CSS puts alpha last
        }
        var fn = Regex.Match(v, @"^rgba?\(\s*([\d.]+)\s*[, ]\s*([\d.]+)\s*[, ]\s*([\d.]+)\s*(?:[,/]\s*([\d.]+%?)\s*)?\)$");
        if (!fn.Success) return null;
        byte Channel(int i) => (byte)Math.Clamp(Math.Round(double.Parse(fn.Groups[i].Value, CultureInfo.InvariantCulture)), 0, 255);
        var alpha = fn.Groups[4].Success
            ? fn.Groups[4].Value.EndsWith('%') ? double.Parse(fn.Groups[4].Value.TrimEnd('%'), CultureInfo.InvariantCulture) / 100
              : double.Parse(fn.Groups[4].Value, CultureInfo.InvariantCulture)
            : 1;
        return ((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), Channel(1), Channel(2), Channel(3));
    }
}
