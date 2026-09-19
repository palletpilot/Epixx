using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lagerkraft.Integrations.Data;
using Lagerkraft.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;

namespace Lagerkraft.Integrations.Tests;

[Collection(IntegrationsCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WebhookDeliveryTests : IAsyncLifetime
{
    private readonly IntegrationsFixture _fx;
    private IntegrationsApiFactory _factory = null!;
    private Guid _tenantId;

    public WebhookDeliveryTests(IntegrationsFixture fx) => _fx = fx;

    public Task InitializeAsync()
    {
        _tenantId = Ids.New();
        _factory = new IntegrationsApiFactory
        {
            PlatformConnectionString = _fx.PlatformConnectionString,
            NatsUrl = _fx.NatsUrl
        };
        _ = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Consume_DuplicateEvent_DeliversOnce()
    {
        await using var sink = await WebhookSink.StartAsync();
        var endpointId = await SeedEndpointAsync(sink.Url, ["inventory.task.created"]);
        var eventId = Ids.New();
        var payload = CloudEventJson(eventId, "inventory.task.created");

        await PublishAsync(eventId, payload, msgId: Ids.New());
        await _factory.Consumer.ConsumeOnceAsync(CancellationToken.None);
        await _factory.Delivery.DeliverDueAsync(CancellationToken.None);

        await PublishAsync(eventId, payload, msgId: Ids.New());
        await _factory.Consumer.ConsumeOnceAsync(CancellationToken.None);
        await _factory.Delivery.DeliverDueAsync(CancellationToken.None);

        sink.Calls.Count.ShouldBe(1);
        await using var db = OpenDb();
        (await db.ProcessedEvents.CountAsync(e =>
            e.EventId == eventId && e.Consumer == "integrations")).ShouldBe(1);
        (await db.WebhookDeliveries.CountAsync(d => d.WebhookEndpointId == endpointId)).ShouldBe(1);
    }

    [Fact]
    public async Task Deliver_Failures_FollowRetryScheduleThenDeadLetter()
    {
        await using var sink = await WebhookSink.StartAsync();
        sink.StatusCode = StatusCodes.Status500InternalServerError;
        var endpointId = await SeedEndpointAsync(sink.Url, ["inventory.task.created"]);
        var eventId = Ids.New();
        await PublishAsync(eventId, CloudEventJson(eventId, "inventory.task.created"), Ids.New());
        await _factory.Consumer.ConsumeOnceAsync(CancellationToken.None);

        TimeSpan[] delays =
        [
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(12)
        ];

        await _factory.Delivery.DeliverDueAsync(CancellationToken.None);
        await AssertDeliveryAsync(endpointId, attempt: 1, status: "failed", deadLettered: false);

        for (var i = 0; i < delays.Length; i++)
        {
            await _factory.Delivery.DeliverDueAsync(CancellationToken.None);
            await AssertDeliveryAsync(endpointId, attempt: i + 1, status: "failed", deadLettered: false);
            _factory.Clock.Advance(delays[i]);
            await _factory.Delivery.DeliverDueAsync(CancellationToken.None);
        }

        await AssertDeliveryAsync(endpointId, attempt: 6, status: "dead_lettered", deadLettered: true);
        sink.Calls.Count.ShouldBe(6);
    }

    [Fact]
    public async Task Deliver_Success_WritesHmacSha256Signature()
    {
        await using var sink = await WebhookSink.StartAsync();
        const string secret = "whsec_test_secret";
        var endpointId = await SeedEndpointAsync(sink.Url, ["inventory.task.created"], secret);
        var eventId = Ids.New();
        var payload = CloudEventJson(eventId, "inventory.task.created");
        await PublishAsync(eventId, payload, Ids.New());
        await _factory.Consumer.ConsumeOnceAsync(CancellationToken.None);
        await _factory.Delivery.DeliverDueAsync(CancellationToken.None);

        sink.Calls.Count.ShouldBe(1);
        var call = sink.Calls.Single();
        var hex = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(call.Body)))
            .ToLowerInvariant();
        call.Signature.ShouldBe($"sha256={hex}");
        call.Body.ShouldBe(payload);
        await using var db = OpenDb();
        var row = await db.WebhookDeliveries.SingleAsync(d => d.WebhookEndpointId == endpointId);
        row.Status.ShouldBe("delivered");
    }

    private async Task AssertDeliveryAsync(Guid endpointId, int attempt, string status, bool deadLettered)
    {
        await using var db = OpenDb();
        var row = await db.WebhookDeliveries.SingleAsync(d => d.WebhookEndpointId == endpointId);
        row.Attempt.ShouldBe(attempt);
        row.Status.ShouldBe(status);
        (row.DeadLetteredAt is not null).ShouldBe(deadLettered);
    }

    private async Task<Guid> SeedEndpointAsync(string url, string[] events, string secret = "secret")
    {
        await using var db = OpenDb();
        var id = Ids.New();
        db.WebhookEndpoints.Add(new WebhookEndpoint
        {
            Id = id,
            TenantId = _tenantId,
            Url = url,
            Secret = secret,
            Events = events,
            Active = true
        });
        await db.SaveChangesAsync();
        return id;
    }

    private IntegrationsDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<IntegrationsDbContext>()
            .UseNpgsql(_fx.PlatformConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new IntegrationsDbContext(options);
    }

    private async Task PublishAsync(Guid eventId, string payload, Guid msgId)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = _fx.NatsUrl });
        await connection.ConnectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateOrUpdateStreamAsync(new NATS.Client.JetStream.Models.StreamConfig("LAGERKRAFT", ["lagerkraft.>"])
        {
            Storage = NATS.Client.JetStream.Models.StreamConfigStorage.File,
            DuplicateWindow = TimeSpan.FromMinutes(10)
        });
        var subject = $"lagerkraft.{_tenantId:D}.inventory.task.created";
        var ack = await js.PublishAsync(
            subject,
            Encoding.UTF8.GetBytes(payload),
            opts: new NatsJSPubOpts { MsgId = msgId.ToString("D") });
        ack.EnsureSuccess();
        _ = eventId;
    }

    private static string CloudEventJson(Guid id, string type) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["specversion"] = "1.0",
            ["id"] = id.ToString("D"),
            ["source"] = "/wms-core",
            ["type"] = type,
            ["time"] = "2026-09-12T12:00:00Z",
            ["data"] = new Dictionary<string, bool> { ["ok"] = true }
        });
}
