using System.Security.Claims;
using System.Text.Json;
using Lagerkraft.SyncGateway.Auth;
using Lagerkraft.SyncGateway.Clients;
using Microsoft.AspNetCore.Mvc;

namespace Lagerkraft.SyncGateway.Sync.Snapshot;

public static class SnapshotEndpoint
{
    public static RouteGroupBuilder MapSyncSnapshot(this RouteGroupBuilder sync)
    {
        sync.MapGet("/snapshot", GetSnapshot).RequireAuthorization();
        return sync;
    }

    private static async Task<IResult> GetSnapshot(
        [FromQuery] Guid? warehouse,
        [FromQuery] string? entity,
        [FromQuery] int? page,
        ClaimsPrincipal principal,
        IPlatformSyncClient platform,
        IWmsCoreSyncClient wms,
        IConfiguration config,
        HttpContext http,
        CancellationToken ct)
    {
        await CompatHeaders.ApplyCompatAsync(http.Response, wms, config, ct);

        if (!SyncAuthExtensions.TryReadSyncPrincipal(principal, out var syncUser))
        {
            return Results.Unauthorized();
        }

        if (await SessionGuard.IsStaleAsync(syncUser, platform, ct))
        {
            return Results.Unauthorized();
        }

        var body = await wms.GetSnapshotAsync(syncUser.TenantId, warehouse, entity, page, ct);
        if (body.ValueKind == JsonValueKind.Object && !body.TryGetProperty("snapshot_schema", out _))
        {
            using var stream = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var prop in body.EnumerateObject())
                {
                    prop.WriteTo(writer);
                }

                writer.WriteString("snapshot_schema", config["Compat:SnapshotSchema"] ?? "Task");
                writer.WriteEndObject();
            }

            stream.Position = 0;
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Results.Json(doc.RootElement.Clone());
        }

        return Results.Json(body);
    }
}
