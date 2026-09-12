using System.Security.Claims;
using System.Text.Json.Serialization;
using Lagerkraft.SyncGateway.Auth;
using Lagerkraft.SyncGateway.Clients;
using Microsoft.AspNetCore.Mvc;

namespace Lagerkraft.SyncGateway.Sync.Beacon;

public static class BeaconEndpoint
{
    public static RouteGroupBuilder MapSyncBeacon(this RouteGroupBuilder sync)
    {
        sync.MapPost("/beacon", PostBeacon).RequireAuthorization();
        return sync;
    }

    private static async Task<IResult> PostBeacon(
        [FromBody] SyncBeaconRequest body,
        ClaimsPrincipal principal,
        IPlatformSyncClient platform,
        IWmsCoreSyncClient wms,
        IConfiguration config,
        HttpContext http,
        CancellationToken ct)
    {
        await CompatHeaders.ApplyCompatAsync(http.Response, wms, config, ct);

        if (!SyncAuthExtensions.TryReadSyncPrincipal(principal, out var syncUser) || syncUser.DeviceId is null)
        {
            return Results.Unauthorized();
        }

        if (await SessionGuard.IsStaleAsync(syncUser, platform, ct))
        {
            return Results.Unauthorized();
        }

        var device = await platform.GetDeviceAsync(syncUser.DeviceId.Value, ct);
        if (device is null)
        {
            return Results.NotFound();
        }

        if (device.RevokedAt is not null)
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        await platform.PostBeaconAsync(syncUser.DeviceId.Value, new DeviceBeaconPayload(
            body.PendingCount,
            body.OldestPendingOccurredAt,
            body.LastSyncAt,
            body.SseConnected), ct);

        return Results.NoContent();
    }
}

public sealed record SyncBeaconRequest(
    [property: JsonPropertyName("pending_count")] int PendingCount,
    [property: JsonPropertyName("oldest_pending_occurred_at")] DateTimeOffset? OldestPendingOccurredAt,
    [property: JsonPropertyName("last_sync_at")] DateTimeOffset? LastSyncAt,
    [property: JsonPropertyName("sse_connected")] bool? SseConnected);
