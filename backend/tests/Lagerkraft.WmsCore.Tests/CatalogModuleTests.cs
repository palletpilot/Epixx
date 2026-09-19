using Npgsql;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CatalogModuleTests : IAsyncLifetime
{
    private static readonly string[] UnitCodes =
        ["st", "kg", "g", "l", "ml", "m", "cm", "m2", "m3"];

    private readonly PostgresFixture _postgres;
    private readonly WmsCoreApiFactory _factory = new();
    private Guid _tenantId;
    private string _cs = "";

    public CatalogModuleTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task Migrate_SeedsNineUnits()
    {
        var codes = await LoadUnitCodes();
        codes.ShouldBe(UnitCodes, ignoreOrder: true);
    }

    private async Task<List<string>> LoadUnitCodes()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            select code from unit_of_measure
            order by code
            """,
            conn);
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
}
