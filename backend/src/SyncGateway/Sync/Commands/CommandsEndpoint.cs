using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lagerkraft.SyncGateway.Auth;
using Lagerkraft.SyncGateway.Clients;
using Microsoft.AspNetCore.Mvc;

namespace Lagerkraft.SyncGateway.Sync.Commands;

public static class CommandsEndpoint
{
    private static readonly Meter Meter = new("Lagerkraft.SyncGateway");
    private static readonly Counter<long> AppVersionCounter =
        Meter.CreateCounter<long>("syncgateway_app_version_requests");

    public static RouteGroupBuilder MapSyncCommands(this RouteGroupBuilder sync)
    {
        sync.MapPost("/commands", PostCommands).RequireAuthorization();
        return sync;
    }

    private static async Task<IResult> PostCommands(
        [FromBody] SyncCommandBatchRequest body,
        ClaimsPrincipal principal,
        IPlatformSyncClient platform,
        IWmsCoreSyncClient wms,
        DeviceBatchGate gate,
        IConfiguration config,
        HttpContext http,
        CancellationToken ct)
    {
        await CompatHeaders.ApplyCompatAsync(http.Response, wms, config, ct);
        RecordAppVersion(http);

        if (!SyncAuthExtensions.TryReadSyncPrincipal(principal, out var syncUser))
        {
            return Results.Unauthorized();
        }

        if (syncUser.DeviceId is null)
        {
            return Results.Unauthorized();
        }

        var deviceId = syncUser.DeviceId.Value;
        if (body.Commands.Any(c => c.DeviceId != deviceId))
        {
            return Results.BadRequest(new { error = "device_id_mismatch" });
        }

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

        // Stale sv flush is accepted (spec exception).
        var entitlement = await platform.GetEntitlementAsync(syncUser.TenantId, ct);
        var hold = TenantHold.Evaluate(entitlement);
        if (hold is not null)
        {
            if (entitlement is not null)
            {
                TenantHold.ApplyStateHeaders(http.Response, entitlement);
            }

            return hold;
        }

        if (!gate.TryEnter(deviceId))
        {
            return Results.Conflict(new { error = "batch_in_flight" });
        }

        try
        {
            var batch = new CommandBatchPayload(
                syncUser.TenantId,
                body.Now,
                body.Commands.Select(c => new CommandEnvelopePayload(
                    c.Id, c.Type, c.V, c.Payload, c.OccurredAt, c.DeviceId, c.UserId)).ToList());

            var wmsResult = await wms.PostCommandsAsync(batch, ct);
            try
            {
                await platform.PostBeaconAsync(deviceId, new DeviceBeaconPayload(
                    body.PendingCountAfter ?? 0,
                    body.OldestPendingOccurredAt,
                    DateTimeOffset.UtcNow,
                    body.SseConnected), ct);
            }
            catch
            {
                // Beacon is best-effort after a successful/handled flush.
            }

            return Results.Json(wmsResult.Body, statusCode: (int)wmsResult.StatusCode);
        }
        finally
        {
            gate.Exit(deviceId);
        }
    }

    private static void RecordAppVersion(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue(CompatHeaders.AppVersion, out var v) && v.Count > 0)
        {
            var version = v.ToString();
            AppVersionCounter.Add(1, new KeyValuePair<string, object?>("app_version", version));
        }
    }
}

public sealed record SyncCommandBatchRequest(
    [property: JsonPropertyName("now")] DateTimeOffset Now,
    [property: JsonPropertyName("commands")] List<SyncCommandEnvelope> Commands,
    [property: JsonPropertyName("pending_count_after")] int? PendingCountAfter = null,
    [property: JsonPropertyName("oldest_pending_occurred_at")] DateTimeOffset? OldestPendingOccurredAt = null,
    [property: JsonPropertyName("sse_connected")] bool? SseConnected = null);

public sealed record SyncCommandEnvelope(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("user_id")] Guid UserId);
