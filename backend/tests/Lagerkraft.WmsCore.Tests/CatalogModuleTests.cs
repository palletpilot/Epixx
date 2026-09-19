using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.WmsCore.Catalog.Contracts;
using Npgsql;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CatalogModuleTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

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

    [Fact]
    public async Task CreateArticle_UnknownUom_400()
    {
        using var client = TenantClient();
        var response = await client.PostAsJsonAsync("/articles", new
        {
            id = Guid.CreateVersion7(),
            sku = "KAFFE-500",
            name = "Kaffe",
            base_uom_id = Guid.CreateVersion7()
        }, Json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("error").GetString().ShouldBe("unknown_uom");
        (await CountArticles()).ShouldBe(0);
    }

    [Fact]
    public async Task CreateArticle_SeedsOnePackagingLevelQtyOne()
    {
        using var client = TenantClient();
        var id = Guid.CreateVersion7();
        var response = await client.PostAsJsonAsync("/articles", new
        {
            id,
            sku = "KAFFE-500",
            name = "Kaffe"
        }, Json);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<ArticleDto>(Json);
        dto.ShouldNotBeNull();
        dto.Id.ShouldBe(id);
        dto.Sku.ShouldBe("KAFFE-500");
        dto.Name.ShouldBe("Kaffe");
        dto.Status.ShouldBe("published");
        dto.BaseUomId.ShouldBe(CatalogDefaults.StId);
        dto.QuantityPrecision.ShouldBe(0);
        decimal.Parse(dto.QuantityStep, CultureInfo.InvariantCulture).ShouldBe(1m);
        dto.AllowLoosePick.ShouldBeTrue();
        dto.PackagingLevels.Length.ShouldBe(1);
        var level = dto.PackagingLevels[0];
        level.ArticleId.ShouldBe(id);
        level.Rank.ShouldBe(1);
        level.Name.ShouldBe("st");
        decimal.Parse(level.QtyInBase, CultureInfo.InvariantCulture).ShouldBe(1m);
    }

    [Fact]
    public async Task CreateArticle_DuplicateSku_409()
    {
        using var client = TenantClient();
        (await client.PostAsJsonAsync("/articles", new
        {
            id = Guid.CreateVersion7(),
            sku = "KAFFE-500",
            name = "Kaffe"
        }, Json)).EnsureSuccessStatusCode();

        var duplicate = await client.PostAsJsonAsync("/articles", new
        {
            id = Guid.CreateVersion7(),
            sku = "KAFFE-500",
            name = "Kaffe igen"
        }, Json);

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await duplicate.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("error").GetString().ShouldBe("duplicate_sku");
    }

    [Fact]
    public async Task ListArticles_ReturnsCreated()
    {
        using var client = TenantClient();
        var id = Guid.CreateVersion7();
        (await client.PostAsJsonAsync("/articles", new
        {
            id,
            sku = "KAFFE-500",
            name = "Kaffe"
        }, Json)).EnsureSuccessStatusCode();

        var list = await client.GetAsync("/articles");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await list.Content.ReadFromJsonAsync<List<ArticleDto>>(Json);
        body.ShouldNotBeNull();
        var article = body.ShouldHaveSingleItem();
        article.Id.ShouldBe(id);
        article.Sku.ShouldBe("KAFFE-500");
        article.PackagingLevels.Length.ShouldBe(1);
        decimal.Parse(article.PackagingLevels[0].QtyInBase, CultureInfo.InvariantCulture).ShouldBe(1m);
    }

    [Fact]
    public async Task GetUnits_ReturnsSt()
    {
        using var client = TenantClient();
        var response = await client.GetAsync("/units");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var units = await response.Content.ReadFromJsonAsync<List<UnitOfMeasureDto>>(Json);
        units.ShouldNotBeNull();
        units.ShouldContain(u => u.Id == CatalogDefaults.StId && u.Code == "st");
    }

    [Fact]
    public async Task Snapshot_EntityArticle_ReturnsSkuAndLevel()
    {
        using var client = TenantClient();
        var id = Guid.CreateVersion7();
        (await client.PostAsJsonAsync("/articles", new
        {
            id,
            sku = "KAFFE-500",
            name = "Kaffe"
        }, Json)).EnsureSuccessStatusCode();

        var response = await client.GetAsync($"/internal/snapshot?tenantId={_tenantId}&entity=Article");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("entity").GetString().ShouldBe("Article");
        var item = body.GetProperty("items").EnumerateArray().Single(el => el.GetProperty("id").GetGuid() == id);
        item.GetProperty("sku").GetString().ShouldBe("KAFFE-500");
        var level = item.GetProperty("packaging_levels").EnumerateArray().ShouldHaveSingleItem();
        decimal.Parse(level.GetProperty("qty_in_base").GetString()!, CultureInfo.InvariantCulture).ShouldBe(1m);
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

    private async Task<int> CountArticles()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from article", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private HttpClient TenantClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
        return client;
    }
}
