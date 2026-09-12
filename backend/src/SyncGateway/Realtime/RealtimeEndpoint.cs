using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Lagerkraft.SyncGateway.Auth;
using Lagerkraft.SyncGateway.Clients;
using Lagerkraft.SyncGateway.Sync;
using Microsoft.AspNetCore.Mvc;

namespace Lagerkraft.SyncGateway.Realtime;

public static class RealtimeEndpoint
{
    public const int ResyncBehindThreshold = 500;
    public static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public static WebApplication MapRealtime(this WebApplication app)
    {
        app.MapGet("/realtime", Stream).RequireAuthorization();
        return app;
    }

    private static async Task Stream(
        HttpContext http,
        [FromQuery] Guid? warehouse,
        [FromQuery] long? since,
        ClaimsPrincipal principal,
        IWmsCoreSyncClient wms,
        IPlatformSyncClient platform,
        ITenantEventBus bus,
        ConnectionRegistry registry,
        MembershipWatcher membershipWatcher,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Lagerkraft.SyncGateway.Realtime");
        if (!SyncAuthExtensions.TryReadSyncPrincipal(principal, out var syncUser))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (await SessionGuard.IsStaleAsync(syncUser, platform, ct))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        long cursor = since ?? 0;
        if (http.Request.Headers.TryGetValue("Last-Event-ID", out var last) && long.TryParse(last, out var lastSeq))
        {
            cursor = lastSeq;
        }

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.Body.FlushAsync(ct);

        var writeGate = new SemaphoreSlim(1, 1);
        var conn = registry.Register(syncUser.TenantId, syncUser.UserId, syncUser.SessionVersion, warehouse);
        var buffer = new ConcurrentQueue<TenantBusMessage>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, conn.CloseToken, lifetime.ApplicationStopping);

        await using var sub = await bus.SubscribeAsync(syncUser.TenantId, async (msg, token) =>
        {
            if (msg.IsReconnectHint)
            {
                // NATS pump death only — write resync then close the SSE stream.
                await WriteEventAsync(http, writeGate, "resync", null, """{"reason":"nats_reconnect"}""", token);
                conn.RequestClose("nats_reconnect");
                return;
            }

            if (msg.Subject.Contains("membership_changed", StringComparison.OrdinalIgnoreCase)
                || msg.Payload.Contains("membership_changed", StringComparison.OrdinalIgnoreCase))
            {
                await membershipWatcher.HandleAsync(msg, token);
                return;
            }

            buffer.Enqueue(msg);
        }, linked.Token);

        try
        {
            var changes = await wms.GetChangesAsync(syncUser.TenantId, warehouse, cursor, linked.Token);
            if (changes.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                await WriteEventAsync(http, writeGate, "resync", null, """{"reason":"retention"}""", linked.Token);
                return;
            }

            if (changes.Body is { } body
                && body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("entries", out var entries)
                && entries.ValueKind == JsonValueKind.Array)
            {
                var max = entries.EnumerateArray()
                    .Select(e => e.TryGetProperty("seq", out var s) && s.TryGetInt64(out var v) ? v : 0L)
                    .DefaultIfEmpty(cursor)
                    .Max();

                // Threshold before catch-up emit — avoid flooding then resyncing.
                if (max - cursor > ResyncBehindThreshold)
                {
                    await WriteEventAsync(http, writeGate, "resync", null, """{"reason":"too_far_behind"}""", linked.Token);
                    return;
                }

                await EmitCatchUpAsync(http, writeGate, principal, warehouse, body, linked.Token);
                cursor = Math.Max(cursor, max);
            }

            while (buffer.TryDequeue(out var buffered))
            {
                if (!await EmitLiveAsync(http, writeGate, principal, warehouse, buffered, cursor, linked.Token))
                {
                    return; // resync written; close
                }

                if (buffered.Seq is { } s && s > cursor)
                {
                    cursor = s;
                }
            }

            var heartbeat = Task.Run(async () =>
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(HeartbeatInterval, linked.Token);
                        await WriteCommentAsync(http, writeGate, "heartbeat", linked.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, linked.Token);

            while (!linked.Token.IsCancellationRequested)
            {
                if (buffer.TryDequeue(out var live))
                {
                    if (!await EmitLiveAsync(http, writeGate, principal, warehouse, live, cursor, linked.Token))
                    {
                        break;
                    }

                    if (live.Seq is { } s && s > cursor)
                    {
                        cursor = s;
                    }

                    continue;
                }

                try
                {
                    await Task.Delay(50, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await linked.CancelAsync();
            try { await heartbeat; } catch { /* ignore */ }
        }
        catch (OperationCanceledException)
        {
            // closing
        }
        finally
        {
            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                var retry = Random.Shared.Next(1, 11);
                try
                {
                    await WriteRawAsync(http, writeGate, $"retry: {retry}\n\n", CancellationToken.None);
                }
                catch
                {
                    // client gone
                }
            }

            registry.Unregister(conn);
            log.LogInformation("SSE closed for tenant {TenantId} user {UserId} reason {Reason}",
                syncUser.TenantId, syncUser.UserId, conn.CloseReason ?? "disconnect");
        }
    }

    private static async Task EmitCatchUpAsync(
        HttpContext http,
        SemaphoreSlim writeGate,
        ClaimsPrincipal principal,
        Guid? warehouse,
        JsonElement body,
        CancellationToken ct)
    {
        if (!body.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            var entity = entry.TryGetProperty("entity", out var e) ? e.GetString() ?? "" : "";
            string? required = null;
            if (entry.TryGetProperty("required_permission", out var rp) && rp.ValueKind == JsonValueKind.String)
            {
                required = rp.GetString();
            }

            if (!ChangePermissions.CanSee(principal, entity, required, warehouse))
            {
                continue;
            }

            long? seq = entry.TryGetProperty("seq", out var s) && s.TryGetInt64(out var sv) ? sv : null;
            if (entry.TryGetProperty("recorded_at", out var ra)
                && ra.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ra.GetString(), out var recorded))
            {
                ConnectionRegistry.ObserveDeliveryLag(recorded);
            }

            await WriteEventAsync(http, writeGate, "change", seq?.ToString(), entry.GetRawText(), ct);
        }
    }

    /// <returns>false if a terminal resync was written and the stream should close.</returns>
    private static async Task<bool> EmitLiveAsync(
        HttpContext http,
        SemaphoreSlim writeGate,
        ClaimsPrincipal principal,
        Guid? warehouse,
        TenantBusMessage msg,
        long cursor,
        CancellationToken ct)
    {
        var entity = msg.Entity ?? GuessEntity(msg.Subject);
        if (!ChangePermissions.CanSee(principal, entity, msg.RequiredPermission, warehouse))
        {
            return true;
        }

        if (msg.Seq is { } seq)
        {
            if (seq <= cursor)
            {
                return true;
            }

            if (seq - cursor > ResyncBehindThreshold)
            {
                await WriteEventAsync(http, writeGate, "resync", null, """{"reason":"too_far_behind"}""", ct);
                return false;
            }
        }

        if (msg.RecordedAt is { } recorded)
        {
            ConnectionRegistry.ObserveDeliveryLag(recorded);
        }

        await WriteEventAsync(http, writeGate, "change", msg.Seq?.ToString(), msg.Payload, ct);
        return true;
    }

    private static string GuessEntity(string subject) =>
        subject.Contains("task", StringComparison.OrdinalIgnoreCase) ? "Task" : "unknown";

    private static async Task WriteEventAsync(
        HttpContext http, SemaphoreSlim writeGate, string eventName, string? id, string data, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(id))
        {
            sb.Append("id: ").Append(id).Append('\n');
        }

        sb.Append("event: ").Append(eventName).Append('\n');
        foreach (var line in data.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }

        sb.Append('\n');
        await WriteRawAsync(http, writeGate, sb.ToString(), ct);
    }

    private static Task WriteCommentAsync(HttpContext http, SemaphoreSlim writeGate, string comment, CancellationToken ct) =>
        WriteRawAsync(http, writeGate, $": {comment}\n\n", ct);

    private static async Task WriteRawAsync(HttpContext http, SemaphoreSlim writeGate, string text, CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await http.Response.Body.WriteAsync(bytes, ct);
            await http.Response.Body.FlushAsync(ct);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
