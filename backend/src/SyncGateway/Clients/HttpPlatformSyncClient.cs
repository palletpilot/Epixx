using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lagerkraft.SyncGateway.Clients;

public sealed class HttpPlatformSyncClient(HttpClient http) : IPlatformSyncClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public async Task<InternalDevice?> GetDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"internal/devices/{deviceId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InternalDevice>(Json, ct);
    }

    public async Task<TenantEntitlement?> GetEntitlementAsync(Guid tenantId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"internal/tenants/{tenantId}/entitlement", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<EntitlementDto>(Json, ct);
        return dto is null
            ? null
            : new TenantEntitlement(dto.TenantId, dto.LifecycleState, dto.Maintenance, dto.PlanCode, dto.HardCapUnits);
    }

    public async Task<int?> GetSessionVersionAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"internal/tenants/{tenantId}/users/{userId}/session-version", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<SessionVersionDto>(Json, ct);
        return dto?.SessionVersion;
    }

    public async Task PostBeaconAsync(Guid deviceId, DeviceBeaconPayload beacon, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"internal/devices/{deviceId}/beacon", beacon, Json, ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed record EntitlementDto(
        Guid TenantId,
        string LifecycleState,
        bool Maintenance,
        string PlanCode,
        int? HardCapUnits,
        JsonElement Features);

    private sealed record SessionVersionDto(int SessionVersion);
}
