using System.Security.Cryptography;
using System.Text;

namespace Lagerkraft.Platform.Devices;

public static class DeviceSecretHasher
{
    public static string CreateSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string Hash(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
