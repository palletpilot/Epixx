using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Relay;
using NATS.Client.Core;
using NATS.Net;
using Testcontainers.Nats;

namespace Lagerkraft.WmsCore.Tests;

[Trait("Category", "Integration")]
public sealed class NatsOutboxPublisherTests : IAsyncLifetime
{
    private readonly NatsContainer _nats = new NatsBuilder("nats:2.10")
        .WithCommand("--js", "-m", "8222")
        .Build();

    public Task InitializeAsync() => _nats.StartAsync();

    public Task DisposeAsync() => _nats.DisposeAsync().AsTask();

    [Fact]
    public async Task Publish_Twice_Same_MsgId_Is_Deduped()
    {
        var url = _nats.GetConnectionString();
        await using var connection = new NatsConnection(new NatsOpts { Url = url });
        await connection.ConnectAsync();

        var publisher = new NatsOutboxPublisher(connection);
        var tenant = Guid.Parse("01900000-0000-7000-8000-000000000099");
        var id = Guid.Parse("01900000-0000-7000-8000-0000000000aa");
        var row = new OutboxRow
        {
            Id = id,
            Type = "inventory.task.created",
            Payload = """{"hello":"world"}""",
            OccurredAt = DateTimeOffset.UtcNow
        };

        await publisher.PublishAsync(tenant, row, CancellationToken.None);
        await publisher.PublishAsync(tenant, row, CancellationToken.None);

        var js = connection.CreateJetStreamContext();
        var stream = await js.GetStreamAsync("LAGERKRAFT");
        await stream.RefreshAsync();
        stream.Info.State.Messages.ShouldBe(1);
    }
}