using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Jobs;

public sealed class AssignmentSweepJob : PeriodicJob
{
    private readonly ITenantConnectionCache _connections;
    private readonly IClock _clock;
    private readonly List<Guid> _tenants = [];
    private readonly object _gate = new();

    public AssignmentSweepJob(
        ITenantConnectionCache connections,
        IClock clock,
        ILogger<AssignmentSweepJob> log) : base(clock, log)
    {
        _connections = connections;
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(60);

    public void TrackTenant(Guid tenantId)
    {
        lock (_gate)
        {
            if (!_tenants.Contains(tenantId))
            {
                _tenants.Add(tenantId);
            }
        }
    }

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        Guid[] tenants;
        lock (_gate)
        {
            tenants = _tenants.ToArray();
        }

        foreach (var tenantId in tenants)
        {
            await SweepAsync(tenantId, cancellationToken);
        }
    }

    public Task SweepTenantAsync(Guid tenantId, CancellationToken ct)
    {
        TrackTenant(tenantId);
        return SweepAsync(tenantId, ct);
    }

    private async Task SweepAsync(Guid tenantId, CancellationToken ct)
    {
        var cs = await _connections.GetConnectionStringAsync(tenantId, ct);
        if (cs is null)
        {
            return;
        }

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new TenantDbContext(options);
        var now = _clock.UtcNow;
        var expired = await db.Tasks
            .Where(t => t.Status == "claimed" && t.AssignedUntil != null && t.AssignedUntil < now)
            .ToListAsync(ct);

        foreach (var task in expired)
        {
            task.Status = "open";
            task.AssigneeUserId = null;
            task.AssignedUntil = null;
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                id = task.Id,
                warehouse_id = task.WarehouseId,
                type = task.Type,
                status = task.Status,
                assignee_user_id = task.AssigneeUserId,
                assigned_until = task.AssignedUntil,
                suggested_location_id = task.SuggestedLocationId,
                created_at = task.CreatedAt
            });
            db.ChangeLog.Add(new ChangeLogRow
            {
                Entity = "task",
                Id = task.Id,
                Op = "upsert",
                Payload = json,
                WarehouseId = task.WarehouseId,
                OccurredAt = now,
                RecordedAt = now
            });
            db.Outbox.Add(new OutboxRow
            {
                Id = Ids.New(),
                Type = "inventory.task.released",
                Payload = json,
                OccurredAt = now
            });
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
