using System.Security.Claims;
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

        if (syncUser.DeviceId is { } deviceId)
        {
            var device = await platform.GetDeviceAsync(deviceId, ct);
            if (device is null)
            {
                return Results.NotFound();
            }

            if (device.RevokedAt is not null)
            {
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            if (device.TenantId != syncUser.TenantId)
            {
                return Results.Unauthorized();
            }
        }

        if (await SessionGuard.IsStaleAsync(syncUser, platform, ct))
        {
            return Results.Unauthorized();
        }

        // Proxy wms-core envelope as-is (includes snapshot_schema from C1/ba64fe8).
        var body = await wms.GetSnapshotAsync(syncUser.TenantId, warehouse, entity, page, ct);
        return Results.Json(body);
    }
}
