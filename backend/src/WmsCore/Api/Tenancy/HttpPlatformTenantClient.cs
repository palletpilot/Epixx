using System.Net;
using System.Net.Http.Json;

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

    private sealed record TenantConnectionResponse(string ConnectionString);

    private sealed record MigrationStatusRequest(string? SchemaVersion, string MigrationStatus, string? LastError);
}
