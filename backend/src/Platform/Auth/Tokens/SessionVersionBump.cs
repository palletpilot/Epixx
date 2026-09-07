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
}
