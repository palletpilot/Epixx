using System.Net;

namespace Lagerkraft.Platform.Provisioning;

public sealed class WmsCoreMigrateClient(HttpClient http, IConfiguration configuration) : IWmsCoreMigrateClient
{
    public async Task MigrateAsync(Guid tenantId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/tenants/{tenantId}/migrate");
        var token = configuration["Internal:Token"] ?? "";
        request.Headers.TryAddWithoutValidation("Lagerkraft-Internal-Token", token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"wms-core migrate returned {(int)response.StatusCode}: {body}",
            null,
            response.StatusCode);
    }
}
