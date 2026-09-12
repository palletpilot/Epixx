using System.Net;
using System.Security.Claims;
using Lagerkraft.SyncGateway.Auth;
using Lagerkraft.SyncGateway.Clients;
using Microsoft.AspNetCore.Mvc;

namespace Lagerkraft.SyncGateway.Sync.Changes;

public static class ChangesEndpoint
{
    public static RouteGroupBuilder MapSyncChanges(this RouteGroupBuilder sync)
    {
        sync.MapGet("/changes", GetChanges).RequireAuthorization();
        return sync;
    }

    private static async Task<IResult> GetChanges(
        [FromQuery] Guid? warehouse,
        [FromQuery] long? since,
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

        var result = await wms.GetChangesAsync(syncUser.TenantId, warehouse, since, ct);
        if (result.StatusCode == HttpStatusCode.Gone)
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        if (result.FeedEpoch is { } epoch)
        {
            http.Response.Headers[CompatHeaders.FeedEpoch] = epoch.ToString();
        }

        return Results.Json(result.Body!);
    }
}
