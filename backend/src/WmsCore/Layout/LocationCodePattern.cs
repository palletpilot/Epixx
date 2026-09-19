using System.Text.RegularExpressions;

namespace Lagerkraft.WmsCore.Layout;

public static class LocationCodePattern
{
    public static bool IsValid(string pattern, string type, string code)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var tokens = Parse(pattern);
        var take = TokenCount(type);
        if (take is null || tokens.Count < take)
        {
            return false;
        }

        var regex = Build(tokens.Take(take.Value).ToList());
        return regex.IsMatch(code);
    }

    public static string LtreeLabel(string code) => code.Replace('-', '_');

    private static int? TokenCount(string type) => type switch
    {
        "zone" or "aisle" => 1,
        "rack" => 2,
        "level" => 3,
        "bin" or "floor" or "dock" => 4,
        _ => null
    };

    private static List<(int? Width, string SepBefore)> Parse(string pattern)
    {
        var tokens = new List<(int? Width, string SepBefore)>();
        var i = 0;
        var sep = "";
        while (i < pattern.Length)
        {
            if (pattern[i] != '{')
            {
                return [];
            }

            var close = pattern.IndexOf('}', i);
            if (close < 0)
            {
                return [];
            }

            var inner = pattern[(i + 1)..close];
            var colon = inner.LastIndexOf(':');
            int? width = null;
            if (colon >= 0 && int.TryParse(inner[(colon + 1)..], out var w))
            {
                width = w;
            }

            tokens.Add((width, sep));
            i = close + 1;
            var next = pattern.IndexOf('{', i);
            if (next < 0)
            {
                break;
            }

            sep = pattern[i..next];
            i = next;
        }

        return tokens;
    }

    private static Regex Build(List<(int? Width, string SepBefore)> tokens)
    {
        var parts = new List<string> { "^" };
        foreach (var (width, sep) in tokens)
        {
            parts.Add(Regex.Escape(sep));
            parts.Add(width is { } w ? $"\\d{{{w}}}" : "[A-Za-z0-9]+");
        }

        parts.Add("$");
        return new Regex(string.Concat(parts), RegexOptions.CultureInvariant);
    }
}
