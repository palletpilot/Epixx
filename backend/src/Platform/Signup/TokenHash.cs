using System.Security.Cryptography;
using System.Text;

namespace Lagerkraft.Platform.Signup;

public static class TokenHash
{
    public static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
