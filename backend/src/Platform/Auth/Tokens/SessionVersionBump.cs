using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Auth;

public sealed class SessionVersionBump(PlatformDbContext db, IPlatformEventPublisher events, IClock clock)
{
    public async Task BumpForUserAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var membership = await db.Memberships
            .Where(m => m.UserId == userId && m.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (membership is null)
        {
            return;
        }

        membership.SessionVersion++;
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(tenantId, "tenant", "membership_changed", new
        {
            user_id = userId,
            membership_id = membership.Id,
            session_version = membership.SessionVersion,
            at = clock.UtcNow
        }, ct);
    }

    public async Task BumpForDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        var membershipIds = await db.DeviceSessions.AsNoTracking()
            .Where(s => s.DeviceId == deviceId)
            .Select(s => s.MembershipId)
            .Distinct()
            .ToListAsync(ct);
        if (membershipIds.Count == 0)
        {
            return;
        }

        var memberships = await db.Memberships.Where(m => membershipIds.Contains(m.Id)).ToListAsync(ct);
        foreach (var membership in memberships)
        {
            membership.SessionVersion++;
            await events.PublishAsync(membership.TenantId, "tenant", "membership_changed", new
            {
                user_id = membership.UserId,
                membership_id = membership.Id,
                session_version = membership.SessionVersion,
                device_id = deviceId,
                at = clock.UtcNow
            }, ct);
        }

        await db.SaveChangesAsync(ct);
    }
}