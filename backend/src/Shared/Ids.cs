using System.Text.RegularExpressions;

namespace Lagerkraft.Shared;

public static class Ids
{
    public static Guid New() => Guid.CreateVersion7();
}

public sealed record Slug
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    private static readonly HashSet<string> Reserved =
    [
        "www", "api", "status", "admin", "app", "docs"
    ];

    private static readonly Regex Hyphens = new("-{2,}", RegexOptions.Compiled);

    public string Value { get; }

    private Slug(string value) => Value = value;

    public override string ToString() => Value;

    public static bool TryCreate(string? raw, out Slug slug)
    {
        slug = null!;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = Normalize(raw);
        if (normalized.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        if (Reserved.Contains(normalized))
        {
            return false;
        }

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            {
                continue;
            }

            return false;
        }

        slug = new Slug(normalized);
        return true;
    }

    // Spec: 3-30 [a-z0-9-]. Padding = leading/trailing hyphens and collapsed repeats after lowercasing.
    private static string Normalize(string raw)
    {
        var lower = raw.Trim().ToLowerInvariant().Replace(' ', '-');
        lower = Hyphens.Replace(lower, "-");
        return lower.Trim('-');
    }
}
