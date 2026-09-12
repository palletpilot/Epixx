using System.Reflection;

namespace Lagerkraft.Platform.Signup;

public static class DisposableDomains
{
    private static readonly Lazy<HashSet<string>> Domains = new(Load);

    public static bool IsBlocked(string email)
    {
        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
        {
            return true;
        }

        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        return Domains.Value.Contains(domain);
    }

    private static HashSet<string> Load()
    {
        var asm = typeof(DisposableDomains).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("DisposableDomains.txt", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("DisposableDomains.txt missing.");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                set.Add(trimmed.ToLowerInvariant());
            }
        }

        return set;
    }
}
