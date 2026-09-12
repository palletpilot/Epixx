using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Relay;

public sealed class OutboxReplay(ITenantConnectionCache connections, IOutboxPublisher publisher, ILogger<OutboxReplay> log)
{
    public async Task<int> ReplayAsync(Guid tenantId, Guid fromId, Guid toId, CancellationToken ct)
    {
        var cs = await connections.GetConnectionStringAsync(tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} connection not found");

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new TenantDbContext(options);

        // Inclusive range by occurred_at between the two rows' timestamps when possible; fallback id ordering.
        var from = await db.Outbox.AsNoTracking().FirstOrDefaultAsync(o => o.Id == fromId, ct)
            ?? throw new InvalidOperationException($"from outbox id {fromId} not found");
        var to = await db.Outbox.AsNoTracking().FirstOrDefaultAsync(o => o.Id == toId, ct)
            ?? throw new InvalidOperationException($"to outbox id {toId} not found");

        var start = from.OccurredAt <= to.OccurredAt ? from.OccurredAt : to.OccurredAt;
        var end = from.OccurredAt <= to.OccurredAt ? to.OccurredAt : from.OccurredAt;

        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.OccurredAt >= start && o.OccurredAt <= end)
            .OrderBy(o => o.OccurredAt)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            await publisher.PublishAsync(tenantId, row, ct);
        }

        log.LogInformation("Replayed {Count} outbox rows for tenant {TenantId}", rows.Count, tenantId);
        return rows.Count;
    }
}
