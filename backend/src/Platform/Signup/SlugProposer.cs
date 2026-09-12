using Lagerkraft.Shared;

namespace Lagerkraft.Platform.Signup;

public static class SlugProposer
{
    public static string Propose(string companyName)
    {
        if (Slug.TryCreate(companyName, out var slug))
        {
            return slug.Value;
        }

        var fallback = new string(companyName.ToLowerInvariant()
            .Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            .Take(Slug.MaxLength)
            .ToArray());
        if (fallback.Length < Slug.MinLength)
        {
            fallback = (fallback + "co").PadRight(Slug.MinLength, 'x');
        }

        return Slug.TryCreate(fallback, out var forced) ? forced.Value : "tenant";
    }

    public static string WithSuffix(string baseSlug, int attempt)
    {
        var suffix = attempt.ToString();
        var maxBase = Slug.MaxLength - suffix.Length - 1;
        var truncated = baseSlug.Length <= maxBase ? baseSlug : baseSlug[..maxBase];
        return Slug.TryCreate($"{truncated}-{suffix}", out var slug) ? slug.Value : $"{truncated}{suffix}";
    }
}
