using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LayoutModuleTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] SystemCodes =
        ["RECEIVING", "PICKING", "SHIPPING", "QUARANTINE", "FLOOR", "ADJUSTMENT"];

    private readonly PostgresFixture _postgres;
    private readonly WmsCoreApiFactory _factory = new();
    private Guid _tenantId;
    private string _cs = "";

    public LayoutModuleTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, _tenantId);
        _factory.Platform.ConnectionString = _cs;
        using var client = TenantClient();
        (await client.PostAsync($"/internal/tenants/{_tenantId}/migrate", null)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task CreateWarehouse_SeedsSystemLocations()
    {
        var warehouseId = Guid.CreateVersion7();
        using var client = TenantClient();
        var created = await client.PostAsJsonAsync("/warehouses", new { id = warehouseId, name = "Demo" }, Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        var codes = await LoadSystemCodes(warehouseId);
        codes.ShouldBe(SystemCodes, ignoreOrder: true);
    }

    [Fact]
    public async Task ListWarehouses_ReturnsCreated()
    {
        var warehouseId = Guid.CreateVersion7();
        using var client = TenantClient();
        (await client.PostAsJsonAsync("/warehouses", new { id = warehouseId, name = "Nord" }, Json))
            .EnsureSuccessStatusCode();

        var list = await client.GetAsync("/warehouses");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await list.Content.ReadFromJsonAsync<List<WarehouseListItem>>(Json);
        body.ShouldNotBeNull();
        body.ShouldContain(w => w.Id == warehouseId && w.Name == "Nord");
        body.Single(w => w.Id == warehouseId).CodePattern.ShouldBe("{aisle}-{rack:2}-{level:2}-{bin:2}");
    }

    [Fact]
    public async Task ActivateWarehouse_SetsActivatedAt()
    {
        var warehouseId = Guid.CreateVersion7();
        using var client = TenantClient();
        (await client.PostAsJsonAsync("/warehouses", new { id = warehouseId, name = "Syd" }, Json))
            .EnsureSuccessStatusCode();

        var activated = await client.PostAsync($"/warehouses/{warehouseId}/activate", null);
        activated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await activated.Content.ReadFromJsonAsync<WarehouseListItem>(Json);
        body.ShouldNotBeNull();
        body.ActivatedAt.ShouldNotBeNull();
    }

    private async Task<List<string>> LoadSystemCodes(Guid warehouseId)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            select code from location
            where warehouse_id = @id and is_system
            order by code
            """,
            conn);
        cmd.Parameters.AddWithValue("id", warehouseId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var codes = new List<string>();
        while (await reader.ReadAsync())
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    private HttpClient TenantClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
        return client;
    }

    private sealed record WarehouseListItem(Guid Id, string Name, string? CodePattern, DateTimeOffset? ActivatedAt);
}
