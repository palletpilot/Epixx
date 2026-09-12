using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Relay;

public sealed class OutboxRelay : PeriodicJob
{
    private readonly ITenantConnectionCache _connections;
    private readonly IOutboxPublisher _publisher;
    private readonly IClock _clock;
    private readonly ILogger<OutboxRelay> _log;
    private readonly List<Guid> _tenantIds = [];
    private readonly object _gate = new();

    public OutboxRelay(
        ITenantConnectionCache connections,
        IOutboxPublisher publisher,
        IClock clock,
        ILogger<OutboxRelay> log) : base(clock, log)
    {
        _connections = connections;
        _publisher = publisher;
        _clock = clock;
        _log = log;
    }

    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(200);

    public void TrackTenant(Guid tenantId)
    {
        lock (_gate)
        {
            if (!_tenantIds.Contains(tenantId))
            {
                _tenantIds.Add(tenantId);
            }
        }
    }

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        Guid[] tenants;
        lock (_gate)
        {
            tenants = _tenantIds.ToArray();
        }

        foreach (var tenantId in tenants)
        {
            await RelayTenantAsync(tenantId, cancellationToken);
        }
    }

    public async Task<int> RelayTenantAsync(Guid tenantId, CancellationToken ct)
    {
        TrackTenant(tenantId);
        var cs = await _connections.GetConnectionStringAsync(tenantId, ct);
        if (cs is null)
        {
            return 0;
        }

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new TenantDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.Outbox
            .FromSql($"SELECT * FROM outbox WHERE published_at IS NULL ORDER BY occurred_at FOR UPDATE SKIP LOCKED LIMIT 100")
            .ToListAsync(ct);

        foreach (var row in batch)
        {
            await _publisher.PublishAsync(tenantId, row, ct);
            row.PublishedAt = _clock.UtcNow;
        }

        if (batch.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        if (batch.Count > 0)
        {
            _log.LogInformation("Relayed {Count} outbox rows for tenant {TenantId}", batch.Count, tenantId);
        }

        return batch.Count;
    }
}
