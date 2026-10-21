using System.Security.Cryptography;
using System.Text;

namespace Lagerkraft.WmsCore.Api.Data;

public static class StockBalanceId
{
    public static readonly Guid Namespace = Guid.Parse("9296f5d4-6bd8-44c5-b874-be71680d1d6b");

    public static Guid For(Guid handlingUnitId, Guid articleId)
    {
        var name = $"hu:{handlingUnitId:D}:{articleId:D}".ToLowerInvariant();
        return CreateVersion5(Namespace, Encoding.UTF8.GetBytes(name));
    }

    // RFC 4122 UUID version 5 (SHA-1 of namespace + name).
    internal static Guid CreateVersion5(Guid namespaceId, ReadOnlySpan<byte> name)
    {
        Span<byte> ns = stackalloc byte[16];
        namespaceId.TryWriteBytes(ns);
        SwapEndian(ns);

        var input = new byte[16 + name.Length];
        ns.CopyTo(input);
        name.CopyTo(input.AsSpan(16));
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        SwapEndian(hash[..16]);
        return new Guid(hash[..16]);
    }

    private static void SwapEndian(Span<byte> guid)
    {
        Swap(guid, 0, 3);
        Swap(guid, 1, 2);
        Swap(guid, 4, 5);
        Swap(guid, 6, 7);
    }

    private static void Swap(Span<byte> bytes, int a, int b)
    {
        (bytes[a], bytes[b]) = (bytes[b], bytes[a]);
    }
}
