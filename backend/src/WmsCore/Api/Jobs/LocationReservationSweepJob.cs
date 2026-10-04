using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Jobs;

public sealed class LocationReservationSweepJob : PeriodicJob
{
    private readonly ITenantConnectionCache _connections;
    private readonly IClock _clock;
    private readonly List<Guid> _tenants = [];

    public LocationReservationSweepJob(
        ITenantConnectionCache connections,
        IClock clock,
        ILogger<LocationReservationSweepJob> log) : base(clock, log)
    {
        _connections = connections;
        _clock = clock;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(60);

    public void TrackTenant(Guid tenantId)
    {
        lock (_tenants)
        {
            if (!_tenants.Contains(tenantId))
            {
                _tenants.Add(tenantId);
            }
        }
    }

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        List<Guid> snapshot;
        lock (_tenants)
        {
            snapshot = [.. _tenants];
        }

        foreach (var tenantId in snapshot)
        {
            await SweepAsync(tenantId, cancellationToken);
        }
    }

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

        var expired = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE location_reservation
               SET released = true
               WHERE (location_id, task_id) IN (
                   SELECT location_id, task_id
                   FROM location_reservation
                   WHERE released = false AND expires_at < {now}
                   FOR UPDATE SKIP LOCKED
                   LIMIT 100
               )", cancellationToken: ct);

        if (expired > 0)
        {
            Log.Information("Released {Count} expired location reservations for tenant {TenantId}",
                expired, tenantId);
        }
    }
}
