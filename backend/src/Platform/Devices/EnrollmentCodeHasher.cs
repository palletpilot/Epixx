using System.Security.Cryptography;
using System.Text;

namespace Lagerkraft.Platform.Devices;

public static class EnrollmentCodeHasher
{
    public static string CreateCode()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var n = BitConverter.ToUInt32(bytes) % 1_000_000;
        return n.ToString("D6");
    }

    public static string Hash(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
