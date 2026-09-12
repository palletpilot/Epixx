using System.Net;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Tenancy;

namespace Lagerkraft.WmsCore.Tests;

public sealed class FakePlatformTenantClient : IPlatformTenantClient
{
    public string? ConnectionString { get; set; } = "Host=tenant-db;Database=tenant_x";
    public bool ThrowOnGetConnection { get; set; }
    public HttpStatusCode? GetConnectionStatus { get; set; }
    public List<(Guid TenantId, string? SchemaVersion, string MigrationStatus, string? LastError)> StatusPuts { get; } = new();
    public TenantEntitlement Entitlement { get; set; } = new(Guid.Empty, "Trialing", false, "pro", 1000);
    public List<MembershipAssignment> Memberships { get; set; } = new();

    public Task<string?> GetConnectionAsync(Guid tenantId, CancellationToken ct)
    {
        if (ThrowOnGetConnection)
        {
            throw new HttpRequestException("platform unreachable");
        }

        if (GetConnectionStatus == HttpStatusCode.NotFound)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(ConnectionString);
    }

    public Task PutMigrationStatusAsync(
        Guid tenantId,
        string? schemaVersion,
        string migrationStatus,
        string? lastError,
        CancellationToken ct)
    {
        StatusPuts.Add((tenantId, schemaVersion, migrationStatus, lastError));
        return Task.CompletedTask;
    }

    public Task<TenantEntitlement?> GetEntitlementAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult<TenantEntitlement?>(Entitlement with { TenantId = tenantId });

    public Task<IReadOnlyList<MembershipAssignment>> GetMembershipHistoryAsync(
        Guid tenantId,
        Guid? userId,
        CancellationToken ct)
    {
        IReadOnlyList<MembershipAssignment> list = userId is { } uid
            ? Memberships.Where(m => m.UserId == uid).ToList()
            : Memberships;
        return Task.FromResult(list);
    }
}
