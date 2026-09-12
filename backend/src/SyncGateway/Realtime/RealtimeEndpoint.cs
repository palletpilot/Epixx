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

        if (await SessionGuard.IsStaleAsync(syncUser, http.RequestServices.GetRequiredService<IPlatformSyncClient>(), ct))
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
        http.Response.Headers.ContentEncoding = "identity";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";
        await http.Response.Body.FlushAsync(ct);

        var conn = registry.Register(syncUser.TenantId, syncUser.UserId, syncUser.SessionVersion, warehouse);
        var buffer = new ConcurrentQueue<TenantBusMessage>();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, conn.CloseToken, lifetime.ApplicationStopping);

        await using var sub = await bus.SubscribeAsync(syncUser.TenantId, async (msg, token) =>
        {
            if (msg.IsReconnectHint)
            {
                await WriteEventAsync(http, "resync", null, """{"reason":"nats_reconnect"}""", token);
                return;
            }

            if (msg.Subject.Contains("membership_changed", StringComparison.OrdinalIgnoreCase)
                || msg.Payload.Contains("membership_changed", StringComparison.OrdinalIgnoreCase))
            {
                await membershipWatcher.HandleAsync(msg, token);
            }

            buffer.Enqueue(msg);
        }, linked.Token);

        try
        {
            // Catch-up from wms-core changes
            var changes = await wms.GetChangesAsync(syncUser.TenantId, warehouse, cursor, linked.Token);
            if (changes.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                await WriteEventAsync(http, "resync", null, """{"reason":"retention"}""", linked.Token);
                return;
            }

            if (changes.Body is { } body)
            {
                await EmitCatchUpAsync(http, principal, warehouse, body, linked.Token);
                if (body.ValueKind == JsonValueKind.Object
                    && body.TryGetProperty("entries", out var entries)
                    && entries.ValueKind == JsonValueKind.Array
                    && entries.GetArrayLength() > 0)
                {
                    var max = entries.EnumerateArray()
                        .Select(e => e.TryGetProperty("seq", out var s) && s.TryGetInt64(out var v) ? v : 0L)
                        .DefaultIfEmpty(cursor)
                        .Max();
                    if (max - cursor > ResyncBehindThreshold)
                    {
                        await WriteEventAsync(http, "resync", null, """{"reason":"too_far_behind"}""", linked.Token);
                        return;
                    }

                    cursor = Math.Max(cursor, max);
                }
            }

            // Drain buffer accumulated during catch-up
            while (buffer.TryDequeue(out var buffered))
            {
                await EmitLiveAsync(http, principal, warehouse, buffered, cursor, linked.Token);
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
                        await WriteCommentAsync(http, "heartbeat", linked.Token);
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
                    await EmitLiveAsync(http, principal, warehouse, live, cursor, linked.Token);
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

            linked.Cancel();
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
                    await WriteRawAsync(http, $"retry: {retry}\n\n", CancellationToken.None);
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
            if (entry.TryGetProperty("required_permission", out var rp))
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

            await WriteEventAsync(http, "change", seq?.ToString(), entry.GetRawText(), ct);
        }
    }

    private static async Task EmitLiveAsync(
        HttpContext http,
        ClaimsPrincipal principal,
        Guid? warehouse,
        TenantBusMessage msg,
        long cursor,
        CancellationToken ct)
    {
        if (msg.Subject.Contains("membership_changed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var entity = msg.Entity ?? GuessEntity(msg.Subject);
        if (!ChangePermissions.CanSee(principal, entity, msg.RequiredPermission, warehouse))
        {
            return;
        }

        if (msg.Seq is { } seq)
        {
            if (seq <= cursor)
            {
                return; // already sent in catch-up
            }

            if (seq - cursor > ResyncBehindThreshold)
            {
                await WriteEventAsync(http, "resync", null, """{"reason":"too_far_behind"}""", ct);
                return;
            }
        }

        if (msg.RecordedAt is { } recorded)
        {
            ConnectionRegistry.ObserveDeliveryLag(recorded);
        }

        await WriteEventAsync(http, "change", msg.Seq?.ToString(), msg.Payload, ct);
    }

    private static string GuessEntity(string subject)
    {
        if (subject.Contains("task", StringComparison.OrdinalIgnoreCase))
        {
            return "Task";
        }

        return "unknown";
    }

    private static async Task WriteEventAsync(HttpContext http, string eventName, string? id, string data, CancellationToken ct)
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
        await WriteRawAsync(http, sb.ToString(), ct);
    }

    private static Task WriteCommentAsync(HttpContext http, string comment, CancellationToken ct) =>
        WriteRawAsync(http, $": {comment}\n\n", ct);

    private static async Task WriteRawAsync(HttpContext http, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await http.Response.Body.WriteAsync(bytes, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
