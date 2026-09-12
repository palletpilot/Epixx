using NATS.Client.Core;

namespace Lagerkraft.WmsCore.Api.Relay;

/// <summary>Creates JetStream stream LAGERKRAFT on startup when NATS is configured.</summary>
public sealed class JetStreamBootstrap(IOutboxPublisher publisher, ILogger<JetStreamBootstrap> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (publisher is NatsOutboxPublisher nats)
        {
            await nats.EnsureStreamAsync(cancellationToken);
            log.LogInformation("JetStream stream LAGERKRAFT ready");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}