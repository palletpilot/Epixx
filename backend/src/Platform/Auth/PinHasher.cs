using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace Lagerkraft.Platform.Auth;

public static class PinHasher
{
    private const int Iterations = 100_000;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var bytes = KeyDerivation.Pbkdf2(pin, salt, KeyDerivationPrf.HMACSHA256, Iterations, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(bytes)}";
    }

    public static bool Verify(string pin, string hash)
    {
        var parts = hash.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = KeyDerivation.Pbkdf2(pin, salt, KeyDerivationPrf.HMACSHA256, iterations, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
