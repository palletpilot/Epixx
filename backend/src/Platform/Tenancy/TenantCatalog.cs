using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Tenancy;

public sealed class TenantCatalog(PlatformDbContext db, ConnectionStringProtector protector, IClock clock)
{
    public Task<Tenant?> FindAsync(Guid id, CancellationToken ct) =>
        db.Tenants.AsNoTracking().Where(t => t.Id == id).FirstOrDefaultAsync(ct);

    public async Task<string?> GetConnectionAsync(Guid id, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.WrappedDek, t.ConnectionCiphertext })
            .FirstOrDefaultAsync(ct);
        if (tenant?.WrappedDek is null || tenant.ConnectionCiphertext is null)
        {
            return null;
        }

        return protector.Decrypt(tenant.WrappedDek, tenant.ConnectionCiphertext);
    }

    public async Task<bool> UpdateMigrationStatusAsync(
        Guid id,
        string? schemaVersion,
        string migrationStatus,
        string? lastError,
        CancellationToken ct)
    {
        var tenant = await db.Tenants.Where(t => t.Id == id).FirstOrDefaultAsync(ct);
        if (tenant is null)
        {
            return false;
        }

        tenant.SchemaVersion = schemaVersion;
        tenant.MigrationStatus = migrationStatus;
        tenant.LastError = lastError;
        tenant.LastAttemptAt = clock.UtcNow;
        if (string.Equals(migrationStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            tenant.Maintenance = true;
        }
        else if (string.Equals(migrationStatus, "up_to_date", StringComparison.OrdinalIgnoreCase))
        {
            tenant.Maintenance = false;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }
}
