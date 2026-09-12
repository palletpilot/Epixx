namespace Lagerkraft.WmsCore.Api.Tenancy;

public interface IPlatformTenantClient
{
    Task<string?> GetConnectionAsync(Guid tenantId, CancellationToken ct);

    Task PutMigrationStatusAsync(
        Guid tenantId,
        string? schemaVersion,
        string migrationStatus,
        string? lastError,
        CancellationToken ct);
}
