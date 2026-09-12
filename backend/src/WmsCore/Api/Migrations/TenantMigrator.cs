using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Migrations;

public interface ITenantMigrator
{
    Task MigrateTenantAsync(Guid tenantId, CancellationToken ct);
}

public sealed class TenantMigrator(
    IPlatformTenantClient platform,
    ITenantConnectionCache connections,
    ILogger<TenantMigrator> log) : ITenantMigrator
{
    public async Task MigrateTenantAsync(Guid tenantId, CancellationToken ct)
    {
        await platform.PutMigrationStatusAsync(tenantId, null, "migrating", null, ct);

        try
        {
            var connection = await connections.GetConnectionStringAsync(tenantId, ct)
                ?? throw new InvalidOperationException($"Tenant {tenantId} connection not found");

            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseNpgsql(connection)
                .UseSnakeCaseNamingConvention()
                .Options;

            await using var db = new TenantDbContext(options);
            await db.Database.MigrateAsync(ct);

            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "none";
            var meta = await db.TenantMeta.SingleOrDefaultAsync(ct);
            if (meta is null)
            {
                db.TenantMeta.Add(new TenantMeta { Id = 1, SchemaVersion = applied });
            }
            else
            {
                meta.SchemaVersion = applied;
            }

            await db.SaveChangesAsync(ct);
            await platform.PutMigrationStatusAsync(tenantId, applied, "up_to_date", null, ct);
            log.LogInformation("Migrated tenant {TenantId} to {SchemaVersion}", tenantId, applied);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Migration failed for tenant {TenantId}", tenantId);
            try
            {
                await platform.PutMigrationStatusAsync(tenantId, null, "failed", ex.Message, ct);
            }
            catch (Exception statusEx)
            {
                log.LogError(statusEx, "Failed to report migration status for {TenantId}", tenantId);
            }

            throw;
        }
    }
}
