using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Relay;

public sealed class OutboxRetentionJob : PeriodicJob
{
    private readonly ITenantConnectionCache _connections;
    private readonly IClock _clock;
    private readonly List<Guid> _tenantIds = [];
    private readonly object _gate = new();

    public OutboxRetentionJob(
        ITenantConnectionCache connections,
        IClock clock,
        ILogger<OutboxRetentionJob> log) : base(clock, log)
    {
        _connections = connections;
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

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
            await PurgeTenantAsync(tenantId, cancellationToken);
        }
    }

    public async Task<int> PurgeTenantAsync(Guid tenantId, CancellationToken ct)
    {
        TrackTenant(tenantId);
        var cs = await _connections.GetConnectionStringAsync(tenantId, ct);
        if (cs is null)
        {
            return 0;
        }

        var cutoff = _clock.UtcNow.AddDays(-30);
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new TenantDbContext(options);
        var outbox = await db.Outbox.Where(o => o.PublishedAt != null && o.PublishedAt < cutoff).ExecuteDeleteAsync(ct);
        var changeLog = await db.ChangeLog.Where(c => c.RecordedAt < cutoff).ExecuteDeleteAsync(ct);
        return outbox + changeLog;
    }
}
