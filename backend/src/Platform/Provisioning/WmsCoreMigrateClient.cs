using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Lagerkraft.Platform.Provisioning;

public sealed class WmsCoreMigrateClient(HttpClient http, IConfiguration configuration) : IWmsCoreMigrateClient
{
    /// <summary>Fixed warehouse id for the SP0 ugly floor shell (Dev/Testing provision seed).</summary>
    public static readonly Guid DevShellWarehouseId =
        Guid.Parse("01900000-0000-7000-8000-000000000001");

    public async Task MigrateAsync(Guid tenantId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/tenants/{tenantId}/migrate");
        var token = configuration["Internal:Token"] ?? "";
        request.Headers.TryAddWithoutValidation("Lagerkraft-Internal-Token", token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"wms-core migrate returned {(int)response.StatusCode}: {body}",
            null,
            response.StatusCode);
    }

    public async Task EnsureDevWarehouseAsync(Guid tenantId, Guid warehouseId, string name, CancellationToken ct)
    {
        var token = configuration["Internal:Token"] ?? "";
        using (var get = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/internal/warehouses/{warehouseId}?tenantId={tenantId}"))
        {
            get.Headers.TryAddWithoutValidation("Lagerkraft-Internal-Token", token);
            using var existing = await http.SendAsync(get, ct);
            if (existing.StatusCode == HttpStatusCode.OK)
            {
                return;
            }
        }

        using var create = new HttpRequestMessage(HttpMethod.Post, "/warehouses")
        {
            Content = JsonContent.Create(
                new DevWarehouseBody(warehouseId, name, "A-01-01", 30, false, false, false))
        };
        create.Headers.TryAddWithoutValidation("Lagerkraft-Tenant-Id", tenantId.ToString());
        using var response = await http.SendAsync(create, ct);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"wms-core create warehouse returned {(int)response.StatusCode}: {body}",
            null,
            response.StatusCode);
    }

    private sealed record DevWarehouseBody(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("codePattern")] string? CodePattern,
        [property: JsonPropertyName("claimMinutes")] int? ClaimMinutes,
        [property: JsonPropertyName("nightShift")] bool NightShift,
        [property: JsonPropertyName("blindCount")] bool BlindCount,
        [property: JsonPropertyName("zonePicking")] bool ZonePicking);
}
