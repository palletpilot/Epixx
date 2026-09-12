using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Relay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboxRelayTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly WmsCoreApiFactory _factory = new();
    private readonly FakeClock _clock = new();
    private Guid _tenantId;
    private string _cs = "";

    public OutboxRelayTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, _tenantId);
        _factory.Platform.ConnectionString = _cs;
        _factory.Clock = _clock;
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        (await client.PostAsync($"/internal/tenants/{_tenantId}/migrate", null)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Relay_PublishesUnpublished_AndMarksPublished()
    {
        var id1 = await SeedOutbox("inventory.task.created");
        var id2 = await SeedOutbox("inventory.task.claimed");

        var relay = _factory.Services.GetRequiredService<OutboxRelay>();
        var n = await relay.RelayTenantAsync(_tenantId, CancellationToken.None);
        n.ShouldBe(2);
        _factory.Publisher.Published.Count.ShouldBe(2);
        _factory.Publisher.Published.Select(p => p.Message.Id).ShouldBe([id1, id2], ignoreOrder: true);

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);
        (await db.Outbox.CountAsync(o => o.PublishedAt == null)).ShouldBe(0);
    }

    [Fact]
    public async Task Relay_RestartAfterPartial_DoesNotDuplicate()
    {
        var id1 = await SeedOutbox("inventory.task.created");
        var id2 = await SeedOutbox("inventory.task.released");

        // Simulate mid-batch: publish first manually and mark published, leave second
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using (var db = new TenantDbContext(options))
        {
            var row = await db.Outbox.SingleAsync(o => o.Id == id1);
            row.PublishedAt = _clock.UtcNow;
            await db.SaveChangesAsync();
        }

        _factory.Publisher.Published.Clear();
        var relay = _factory.Services.GetRequiredService<OutboxRelay>();
        var n = await relay.RelayTenantAsync(_tenantId, CancellationToken.None);
        n.ShouldBe(1);
        _factory.Publisher.Published.Count.ShouldBe(1);
        _factory.Publisher.Published[0].Message.Id.ShouldBe(id2);

        // Second relay publishes nothing
        _factory.Publisher.Published.Clear();
        (await relay.RelayTenantAsync(_tenantId, CancellationToken.None)).ShouldBe(0);
        _factory.Publisher.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Replay_RePublishesRange_WithoutClearingPublishedAt()
    {
        var id1 = await SeedOutbox("inventory.task.created");
        var id2 = await SeedOutbox("inventory.task.completed");
        var relay = _factory.Services.GetRequiredService<OutboxRelay>();
        await relay.RelayTenantAsync(_tenantId, CancellationToken.None);
        _factory.Publisher.Published.Clear();

        var replay = _factory.Services.GetRequiredService<OutboxReplay>();
        var n = await replay.ReplayAsync(_tenantId, id1, id2, CancellationToken.None);
        n.ShouldBe(2);
        _factory.Publisher.Published.Count.ShouldBe(2);

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);
        (await db.Outbox.CountAsync(o => o.PublishedAt == null)).ShouldBe(0);
    }

    [Fact]
    public async Task SubjectFor_UsesTenantAndOutboxType()
    {
        var tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
        NatsOutboxPublisher.SubjectFor(tenant, "inventory.task.created")
            .ShouldBe($"lagerkraft.{tenant:D}.inventory.task.created");
    }

    private async Task<Guid> SeedOutbox(string type)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);
        var id = Ids.New();
        db.Outbox.Add(new OutboxRow
        {
            Id = id,
            Type = type,
            Payload = """{"ok":true}""",
            OccurredAt = _clock.UtcNow
        });
        await db.SaveChangesAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        return id;
    }
}
