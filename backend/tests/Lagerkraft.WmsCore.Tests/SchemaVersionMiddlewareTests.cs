using System.Net;
using Lagerkraft.WmsCore.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SchemaVersionMiddlewareTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly WmsCoreApiFactory _factory = new();

    public SchemaVersionMiddlewareTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Write_WhenSchemaMatches_Continues()
    {
        var tenantId = Guid.CreateVersion7();
        _factory.Platform.ConnectionString = await TenantDatabases.CreateAsync(_postgres.ConnectionString, tenantId);
        using var client = InternalClient();
        (await client.PostAsync($"/internal/tenants/{tenantId}/migrate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", tenantId.ToString());
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent("{}")
        });
        response.StatusCode.ShouldNotBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Write_WhenSchemaMismatch_Returns503()
    {
        var tenantId = Guid.CreateVersion7();
        var cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, tenantId);
        _factory.Platform.ConnectionString = cs;
        using var client = InternalClient();
        (await client.PostAsync($"/internal/tenants/{tenantId}/migrate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new TenantDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "__EFMigrationsHistory";""");
        }
        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", tenantId.ToString());
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/anything")
        {
            Content = new StringContent("{}")
        });
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.Contains("Lagerkraft-Tenant-Maintenance").ShouldBeTrue();
    }

    [Fact]
    public async Task Read_WhenSchemaMismatch_Continues()
    {
        var tenantId = Guid.CreateVersion7();
        var cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, tenantId);
        _factory.Platform.ConnectionString = cs;
        using var client = InternalClient();
        (await client.PostAsync($"/internal/tenants/{tenantId}/migrate", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new TenantDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "__EFMigrationsHistory";""");
        }

        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", tenantId.ToString());
        var response = await client.GetAsync("/");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        return client;
    }
}

