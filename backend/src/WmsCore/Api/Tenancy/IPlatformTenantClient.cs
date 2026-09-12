using Lagerkraft.WmsCore.Api.Commands;

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

    Task<TenantEntitlement?> GetEntitlementAsync(Guid tenantId, CancellationToken ct);

    Task<IReadOnlyList<MembershipAssignment>> GetMembershipHistoryAsync(
        Guid tenantId,
        Guid? userId,
        CancellationToken ct);
}
