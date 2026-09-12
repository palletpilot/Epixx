using Lagerkraft.WmsCore.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Migrations;

public static class PostMigrationVerify
{
    public static async Task VerifyAsync(TenantDbContext db, CancellationToken ct)
    {
        // Row-count manifests arrive with RequiresSnapshot migrations; FK sanity for SP0 tables.
        _ = await db.TenantMeta.CountAsync(ct);
        _ = await db.ChangeLog.CountAsync(ct);
        _ = await db.Outbox.CountAsync(ct);
        _ = await db.ProcessedCommands.CountAsync(ct);
    }
}
