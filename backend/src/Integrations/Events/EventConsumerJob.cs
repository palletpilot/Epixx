using System.Text;
using System.Text.Json;
using Lagerkraft.Integrations.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Npgsql;

namespace Lagerkraft.Integrations.Events;

public sealed class EventConsumerJob : PeriodicJob
{
    private readonly INatsConnection _nats;
    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;
    private readonly ILogger<EventConsumerJob> _log;
    private readonly SemaphoreSlim _ready = new(1, 1);
    private INatsJSConsumer? _consumer;

    public EventConsumerJob(
        INatsConnection nats,
        IServiceScopeFactory scopes,
        IClock clock,
        ILogger<EventConsumerJob> log) : base(clock, log)
    {
        _nats = nats;
        _scopes = scopes;
        _clock = clock;
        _log = log;
    }

    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(200);

    public Task ConsumeOnceAsync(CancellationToken ct) => RunOnceAsync(ct);

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var consumer = await EnsureConsumerAsync(cancellationToken);
        await foreach (var msg in consumer.FetchAsync<byte[]>(
                           new NatsJSFetchOpts { MaxMsgs = 32, Expires = TimeSpan.FromSeconds(2) },
                           cancellationToken: cancellationToken))
        {
            try
            {
                await HandleAsync(msg, cancellationToken);
                await msg.AckAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to process integrations event {Subject}", msg.Subject);
                await msg.NakAsync(cancellationToken: cancellationToken);
            }
        }
    }

    private async Task HandleAsync(INatsJSMsg<byte[]> msg, CancellationToken ct)
    {
        if (!TryParseSubject(msg.Subject, out var tenantId, out var eventType))
        {
            return;
        }

        var payload = msg.Data is { Length: > 0 } ? Encoding.UTF8.GetString(msg.Data) : "{}";
        var eventId = ReadEventId(msg, payload);
        if (eventId is null)
        {
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = eventId.Value,
            Consumer = IntegrationsDbContext.ConsumerName,
            At = _clock.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            return;
        }

        var endpoints = await db.WebhookEndpoints
            .Where(e => e.TenantId == tenantId && e.Active && e.Events.Contains(eventType))
            .ToListAsync(ct);

        foreach (var endpoint in endpoints)
        {
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                Id = Ids.New(),
                WebhookEndpointId = endpoint.Id,
                EventType = eventType,
                Payload = payload,
                Status = "pending",
                Attempt = 0,
                NextAttemptAt = _clock.UtcNow,
                CreatedAt = _clock.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<INatsJSConsumer> EnsureConsumerAsync(CancellationToken ct)
    {
        if (_consumer is not null)
        {
            return _consumer;
        }

        await _ready.WaitAsync(ct);
        try
        {
            if (_consumer is not null)
            {
                return _consumer;
            }

            var js = _nats.CreateJetStreamContext();
            await js.CreateOrUpdateStreamAsync(
                new StreamConfig("LAGERKRAFT", ["lagerkraft.>"])
                {
                    Storage = StreamConfigStorage.File,
                    DuplicateWindow = TimeSpan.FromMinutes(10)
                },
                ct);
            _consumer = await js.CreateOrUpdateConsumerAsync("LAGERKRAFT", new ConsumerConfig
            {
                Name = IntegrationsDbContext.ConsumerName,
                DurableName = IntegrationsDbContext.ConsumerName,
                FilterSubject = "lagerkraft.>",
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All
            }, ct);
            return _consumer;
        }
        finally
        {
            _ready.Release();
        }
    }

    private static Guid? ReadEventId(INatsJSMsg<byte[]> msg, string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var fromBody))
            {
                return fromBody;
            }
        }
        catch (JsonException)
        {
            // payload is not JSON; fall through to NATS msg id
        }

        var header = msg.Headers?["Nats-Msg-Id"];
        return header is not null && Guid.TryParse(header.ToString(), out var fromHeader) ? fromHeader : null;
    }

    private static bool TryParseSubject(string subject, out Guid tenantId, out string eventType)
    {
        tenantId = default;
        eventType = "";
        var parts = subject.Split('.');
        if (parts.Length < 3 || parts[0] != "lagerkraft" || !Guid.TryParse(parts[1], out tenantId))
        {
            return false;
        }

        eventType = string.Join('.', parts.Skip(2));
        return eventType.Length > 0;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
