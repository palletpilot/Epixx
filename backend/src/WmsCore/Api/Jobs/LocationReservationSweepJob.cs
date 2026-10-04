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

    public LocationReservationSweepJob(
        ITenantConnectionCache connections,
        IClock clock,
        ILogger<LocationReservationSweepJob> log) : base(clock, log)
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

        var releasedCount = await db.Database.ExecuteSqlRawAsync(
            @"DELETE FROM location_reservation
              WHERE (location_id, task_id) IN (
                  SELECT location_id, task_id
                  FROM location_reservation
                  WHERE expires_at < {0}
                  FOR UPDATE SKIP LOCKED
                  LIMIT 100
              )", now, cancellationToken: ct);

        if (releasedCount > 0)
        {
            Log.Information("Released {Count} expired location reservations for tenant {TenantId}",
                releasedCount, tenantId);
        }
    }
}
