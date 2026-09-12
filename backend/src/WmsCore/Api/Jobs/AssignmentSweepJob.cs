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

    public AssignmentSweepJob(
        ITenantConnectionCache connections,
        IClock clock,
        ILogger<AssignmentSweepJob> log) : base(clock, log)
    {
        _connections = connections;
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(60);

    protected override Task RunOnceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SweepTenantAsync(Guid tenantId, CancellationToken ct) => SweepAsync(tenantId, ct);

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
