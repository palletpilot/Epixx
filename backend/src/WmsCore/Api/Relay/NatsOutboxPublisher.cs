using Lagerkraft.WmsCore.Api.Data;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace Lagerkraft.WmsCore.Api.Relay;

public interface IOutboxPublisher
{
    Task PublishAsync(Guid tenantId, OutboxRow message, CancellationToken ct);
}

public sealed class NatsOutboxPublisher : IOutboxPublisher
{
    private readonly INatsJSContext _js;
    private readonly SemaphoreSlim _ensure = new(1, 1);
    private bool _streamReady;

    public NatsOutboxPublisher(INatsConnection connection)
    {
        _js = connection.CreateJetStreamContext();
    }

    public async Task PublishAsync(Guid tenantId, OutboxRow message, CancellationToken ct)
    {
        await EnsureStreamAsync(ct);
        var subject = SubjectFor(tenantId, message.Type);
        var opts = new NatsJSPubOpts { MsgId = message.Id.ToString("D") };
        var ack = await _js.PublishAsync(subject, message.Payload, opts: opts, cancellationToken: ct);
        if (!ack.IsSuccess() && !ack.Duplicate)
        {
            ack.EnsureSuccess();
        }
    }

    public async Task EnsureStreamAsync(CancellationToken ct)
    {
        if (_streamReady)
        {
            return;
        }

        await _ensure.WaitAsync(ct);
        try
        {
            if (_streamReady)
            {
                return;
            }

            await _js.CreateOrUpdateStreamAsync(
                new StreamConfig("LAGERKRAFT", ["lagerkraft.>"])
                {
                    Storage = StreamConfigStorage.File,
                    DuplicateWindow = TimeSpan.FromMinutes(10)
                },
                ct);
            _streamReady = true;
        }
        finally
        {
            _ensure.Release();
        }
    }

    public static string SubjectFor(Guid tenantId, string outboxType)
        => $"lagerkraft.{tenantId:D}.{outboxType}";
}