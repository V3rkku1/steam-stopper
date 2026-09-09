using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamStopper.Core;

public static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        var tokens = Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|(\\{)|(\\})")
            .Select(m => m)
            .ToList();
        var index = 0;
        return ParseObject(tokens, ref index);
    }

    public static Dictionary<string, object> AsObject(object? value)
        => value as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    public static string GetString(IDictionary<string, object> map, string key, string fallback = "")
        => map.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback : fallback;

    public static long GetLong(IDictionary<string, object> map, string key)
        => long.TryParse(GetString(map, key, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static Dictionary<string, object> ParseObject(List<Match> tokens, ref int index)
    {
        var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        string? lastKey = null;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (token.Groups[3].Success)
            {
                index++;
                return obj;
            }
            if (token.Groups[2].Success)
            {
                index++;
                var nested = ParseObject(tokens, ref index);
                if (lastKey is null)
                {
                    if (!obj.TryGetValue("_unnamed", out var unnamed))
                    {
                        unnamed = new List<object>();
                        obj["_unnamed"] = unnamed;
                    }
                    ((List<object>)unnamed).Add(nested);
                }
                else
                {
                    obj[lastKey] = nested;
                    lastKey = null;
                }
                continue;
            }

            var raw = Regex.Unescape(token.Groups[1].Value);
            if (lastKey is null)
                lastKey = raw;
            else
            {
                obj[lastKey] = raw;
                lastKey = null;
            }
            index++;
        }
        return obj;
    }
}
