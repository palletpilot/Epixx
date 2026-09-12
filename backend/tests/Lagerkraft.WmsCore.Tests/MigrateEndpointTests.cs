using System.Net;

namespace Lagerkraft.WmsCore.Tests;

[Trait("Category", "Integration")]
public sealed class MigrateEndpointTests : IAsyncLifetime
{
    private readonly WmsCoreApiFactory _factory = new();

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
    public async Task Migrate_HappyPath_Returns200_AndReportsStatus()
    {
        var tenantId = Guid.CreateVersion7();
        using var client = InternalClient();

        var response = await client.PostAsync($"/internal/tenants/{tenantId}/migrate", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.Platform.StatusPuts.Count.ShouldBe(1);
        var put = _factory.Platform.StatusPuts[0];
        put.TenantId.ShouldBe(tenantId);
        put.SchemaVersion.ShouldBe("stub");
        put.MigrationStatus.ShouldBe("up_to_date");
        put.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Migrate_SecondCall_Idempotent200()
    {
        var tenantId = Guid.CreateVersion7();
        using var client = InternalClient();

        var first = await client.PostAsync($"/internal/tenants/{tenantId}/migrate", null);
        var second = await client.PostAsync($"/internal/tenants/{tenantId}/migrate", null);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.Platform.StatusPuts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Migrate_PlatformConnectionNotFound_Returns404()
    {
        _factory.Platform.GetConnectionStatus = HttpStatusCode.NotFound;
        using var client = InternalClient();

        var response = await client.PostAsync($"/internal/tenants/{Guid.CreateVersion7()}/migrate", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _factory.Platform.StatusPuts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Migrate_PlatformConnectionFailure_Returns5xx()
    {
        _factory.Platform.ThrowOnGetConnection = true;
        using var client = InternalClient();

        var response = await client.PostAsync($"/internal/tenants/{Guid.CreateVersion7()}/migrate", null);

        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(500);
        _factory.Platform.StatusPuts.ShouldBeEmpty();
    }

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        return client;
    }
}
