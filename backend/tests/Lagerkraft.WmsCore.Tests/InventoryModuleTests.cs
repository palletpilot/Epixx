using Npgsql;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryModuleTests : IAsyncLifetime
{
    private static readonly string[] Tables =
        ["handling_unit", "handling_unit_content", "stock_movement", "stock_balance", "location_reservation"];

    private readonly PostgresFixture _postgres;
    private readonly WmsCoreApiFactory _factory = new();
    private Guid _tenantId;
    private string _cs = "";

    public InventoryModuleTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task Migrate_CreatesInventoryTables()
    {
        var found = await LoadTables();
        found.ShouldBe(Tables, ignoreOrder: true);
    }

    private async Task<List<string>> LoadTables()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        var names = new List<string>();
        foreach (var table in Tables)
        {
            await using var cmd = new NpgsqlCommand("select to_regclass(('public.' || @name)::text)::text", conn);
            cmd.Parameters.AddWithValue("name", table);
            var value = await cmd.ExecuteScalarAsync();
            if (value is string name)
            {
                names.Add(name.Contains('.') ? name.Split('.')[^1] : name);
            }
        }

        return names;
    }

    private HttpClient TenantClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
        return client;
    }
}
