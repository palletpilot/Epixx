using System.Net;
using Lagerkraft.WmsCore.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MigrateEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly WmsCoreApiFactory _factory = new();

    public MigrateEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_MissingToken_Unauthorized()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync($"/internal/tenants/{Guid.CreateVersion7()}/migrate", null);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Migrate_WrongToken_Unauthorized()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", "wrong");
        var response = await client.PostAsync($"/internal/tenants/{Guid.CreateVersion7()}/migrate", null);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Migrate_HappyPath_AppliesSchema_AndReportsStatus()
    {
        var tenantId = Guid.CreateVersion7();
        var cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, tenantId);
        _factory.Platform.ConnectionString = cs;
        using var client = InternalClient();

        var response = await client.PostAsync($"/internal/tenants/{tenantId}/migrate", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var put = _factory.Platform.StatusPuts.Last(p => p.TenantId == tenantId && p.MigrationStatus == "up_to_date");
        put.SchemaVersion.ShouldNotBeNullOrEmpty();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new TenantDbContext(options);
        (await db.TenantMeta.SingleAsync()).SchemaVersion.ShouldNotBeNullOrEmpty();
        (await db.Database.GetAppliedMigrationsAsync()).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Migrate_SecondCall_Idempotent200()
    {
        var tenantId = Guid.CreateVersion7();
        _factory.Platform.ConnectionString = await TenantDatabases.CreateAsync(_postgres.ConnectionString, tenantId);
        using var client = InternalClient();

        var first = await client.PostAsync($"/internal/tenants/{tenantId}/migrate", null);
        var second = await client.PostAsync($"/internal/tenants/{tenantId}/migrate", null);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.Platform.StatusPuts.Count(p => p.TenantId == tenantId && p.MigrationStatus == "up_to_date")
            .ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Migrate_PlatformConnectionNotFound_Returns404()
    {
        _factory.Platform.GetConnectionStatus = HttpStatusCode.NotFound;
        using var client = InternalClient();

        var response = await client.PostAsync($"/internal/tenants/{Guid.CreateVersion7()}/migrate", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _factory.Platform.GetConnectionStatus = null;
    }

    [Fact]
    public async Task Migrate_PlatformConnectionFailure_Returns5xx()
    {
        _factory.Platform.ThrowOnGetConnection = true;
        using var client = InternalClient();

        var response = await client.PostAsync($"/internal/tenants/{Guid.CreateVersion7()}/migrate", null);

        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(500);
        _factory.Platform.ThrowOnGetConnection = false;
    }

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        return client;
    }
}

internal static class TenantDatabases
{
    public static async Task<string> CreateAsync(string adminConnectionString, Guid tenantId)
    {
        var dbName = "t_" + tenantId.ToString("N");
        await using (var conn = new NpgsqlConnection(adminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = dbName };
        return builder.ConnectionString;
    }
}
