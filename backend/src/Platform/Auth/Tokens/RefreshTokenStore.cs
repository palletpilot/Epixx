using System.Security.Cryptography;
using System.Text;
using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Auth;

public sealed class RefreshTokenStore(PlatformDbContext db, IClock clock)
{
    public async Task<string> IssueAsync(Membership membership, Guid? deviceId, CancellationToken ct)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        db.RefreshTokens.Add(new Data.RefreshToken
        {
            Id = Ids.New(),
            Hash = Hash(raw),
            SessionVersion = membership.SessionVersion,
            ExpiresAt = clock.UtcNow.AddHours(membership.Tenant.RefreshTokenHours),
            DeviceId = deviceId,
            MembershipId = membership.Id
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    public Task<Data.RefreshToken?> FindAsync(string raw, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.Hash == Hash(raw)).FirstOrDefaultAsync(ct);

    public async Task RevokeAsync(string raw, CancellationToken ct)
    {
        var row = await FindAsync(raw, ct);
        if (row is not null)
        {
            db.RefreshTokens.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    public static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}
