using System.Security.Cryptography;
using System.Text;

namespace Lagerkraft.Integrations.Webhooks;

public static class WebhookSignature
{
    public const string HeaderName = "X-Lagerkraft-Signature";

    public static string Compute(string secret, string body)
    {
        var hex = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();
        return $"sha256={hex}";
    }
}
