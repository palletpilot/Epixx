using System.Text;
using System.Text.Json;
using NATS.Client.Core;

namespace Lagerkraft.SyncGateway.Realtime;

public sealed class NatsTenantEventBus(INatsConnection connection, ILogger<NatsTenantEventBus> log) : ITenantEventBus
{
    public async Task<IAsyncDisposable> SubscribeAsync(
        Guid tenantId,
        Func<TenantBusMessage, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        var subject = $"lagerkraft.{tenantId:D}.>";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sub = await connection.SubscribeCoreAsync<byte[]>(subject, cancellationToken: cts.Token);

        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in sub.Msgs.ReadAllAsync(cts.Token))
                {
                    var payload = Encoding.UTF8.GetString(msg.Data ?? []);
                    long? seq = null;
                    string? entity = null;
                    DateTimeOffset? recorded = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
                        if (doc.RootElement.TryGetProperty("seq", out var s) && s.TryGetInt64(out var sv))
                        {
                            seq = sv;
                        }

                        if (doc.RootElement.TryGetProperty("entity", out var e))
                        {
                            entity = e.GetString();
                        }

                        if (doc.RootElement.TryGetProperty("recorded_at", out var r)
                            && r.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(r.GetString(), out var rt))
                        {
                            recorded = rt;
                        }

                        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                        {
                            if (seq is null && data.TryGetProperty("seq", out var ds) && ds.TryGetInt64(out var dsv))
                            {
                                seq = dsv;
                            }

                            if (entity is null && data.TryGetProperty("entity", out var de))
                            {
                                entity = de.GetString();
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // raw payload
                    }

                    await handler(new TenantBusMessage(msg.Subject, payload, seq, entity, null, recorded), cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on dispose
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "NATS subscription ended for {Subject}", subject);
                try
                {
                    await handler(new TenantBusMessage(subject, "", IsReconnectHint: true), CancellationToken.None);
                }
                catch
                {
                    // ignore
                }
            }
        }, cts.Token);

        return new NatsSub(sub, cts, pump);
    }

    private sealed class NatsSub(INatsSub<byte[]> sub, CancellationTokenSource cts, Task pump) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            try
            {
                await pump;
            }
            catch
            {
                // ignored
            }

            await sub.DisposeAsync();
            cts.Dispose();
        }
    }
}
