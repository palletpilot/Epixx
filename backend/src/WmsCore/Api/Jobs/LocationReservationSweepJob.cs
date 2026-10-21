using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Jobs;

public sealed class LocationReservationSweepJob : PeriodicJob
{
    private readonly ITenantConnectionCache _connections;
    private readonly IClock _clock;
    private readonly List<Guid> _tenants = [];
    private readonly object _gate = new();

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
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            CommandDispatcher.LockKey(tenantId));
        var now = _clock.UtcNow;
        var expired = await db.LocationReservations.Where(r => r.ExpiresAt <= now).ToListAsync(ct);
        if (expired.Count > 0)
        {
            db.LocationReservations.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
