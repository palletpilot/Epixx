extern alias platform;

using Lagerkraft.Shared;
using Microsoft.EntityFrameworkCore;
using Testcontainers.Nats;
using Testcontainers.PostgreSql;
using PlatformDbContext = platform::Lagerkraft.Platform.Data.PlatformDbContext;

namespace Lagerkraft.Integrations.Tests;

public sealed class IntegrationsFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("platform")
        .WithUsername("lagerkraft")
        .WithPassword("lagerkraft")
        .Build();

    private readonly NatsContainer _nats = new NatsBuilder("nats:2.10")
        .WithCommand("--js", "-m", "8222")
        .Build();

    public string PlatformConnectionString => _postgres.GetConnectionString();
    public string NatsUrl => _nats.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _nats.StartAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(PlatformConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new PlatformDbContext(options, new FakeClock());
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _nats.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationsCollection : ICollectionFixture<IntegrationsFixture>
{
    public const string Name = "integrations";
}
