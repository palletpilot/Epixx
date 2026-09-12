using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.WmsCore.Api.Commands;

namespace Lagerkraft.WmsCore.Api.Tenancy;

public sealed class HttpPlatformTenantClient(HttpClient http) : IPlatformTenantClient
{
    public async Task<string?> GetConnectionAsync(Guid tenantId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"internal/tenants/{tenantId}/connection", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TenantConnectionResponse>(cancellationToken: ct);
        return body?.ConnectionString;
    }

    public async Task PutMigrationStatusAsync(
        Guid tenantId,
        string? schemaVersion,
        string migrationStatus,
        string? lastError,
        CancellationToken ct)
    {
        using var response = await http.PutAsJsonAsync(
            $"internal/tenants/{tenantId}/migration-status",
            new MigrationStatusRequest(schemaVersion, migrationStatus, lastError),
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<TenantEntitlement?> GetEntitlementAsync(Guid tenantId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"internal/tenants/{tenantId}/entitlement", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EntitlementDto>(cancellationToken: ct);
        return body is null
            ? null
            : new TenantEntitlement(body.TenantId, body.LifecycleState, body.Maintenance, body.PlanCode, body.HardCapUnits);
    }

    public async Task<IReadOnlyList<MembershipAssignment>> GetMembershipHistoryAsync(
        Guid tenantId,
        Guid? userId,
        CancellationToken ct)
    {
        var url = userId is { } uid
            ? $"internal/memberships/{tenantId}/history?userId={uid}"
            : $"internal/memberships/{tenantId}/history";
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<List<MembershipHistoryDto>>(cancellationToken: ct)
            ?? [];
        return body.Select(m => new MembershipAssignment(m.UserId, m.Role, m.WarehouseId, m.ValidFrom, m.ValidTo)).ToList();
    }

    private sealed record TenantConnectionResponse(string ConnectionString);
    private sealed record MigrationStatusRequest(string? SchemaVersion, string MigrationStatus, string? LastError);
    private sealed record EntitlementDto(Guid TenantId, string LifecycleState, bool Maintenance, string PlanCode, int? HardCapUnits, JsonElement Features);
    private sealed record MembershipHistoryDto(Guid Id, Guid UserId, string Role, Guid? WarehouseId, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);
}
