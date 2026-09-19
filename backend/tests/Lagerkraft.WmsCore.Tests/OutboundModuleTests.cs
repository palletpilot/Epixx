using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Catalog.Contracts;
using Lagerkraft.WmsCore.Outbound.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboundModuleTests : IAsyncLifetime
{
    private static readonly string[] Tables =
        ["outbound_order", "outbound_order_line", "shipment", "shipment_handling_unit"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly PostgresFixture _postgres;
    private readonly WmsCoreApiFactory _factory = new();
    private readonly FakeClock _clock = new();
    private Guid _tenantId;
    private Guid _workerId;
    private Guid _deviceId;
    private Guid _warehouseId;
    private Guid _articleId;
    private string _cs = "";

    public OutboundModuleTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _workerId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _warehouseId = Guid.CreateVersion7();
        _articleId = Guid.CreateVersion7();
        _cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, _tenantId);
        _factory.Platform.ConnectionString = _cs;
        _factory.Platform.Entitlement = new(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.Memberships =
        [
            new MembershipAssignment(_workerId, "floor_worker", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null)
        ];
        _factory.Clock = _clock;
        using var client = TenantClient();
        (await client.PostAsync($"/internal/tenants/{_tenantId}/migrate", null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/warehouses", new { id = _warehouseId, name = "WH1" }, Json))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/articles", new
        {
            id = _articleId,
            sku = "KAFFE-500",
            name = "Kaffe",
            base_uom_id = CatalogDefaults.StId
        }, Json)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrate_CreatesOutboundTables()
    {
        var found = await LoadTables();
        found.ShouldBe(Tables, ignoreOrder: true);
    }

    [Fact]
    public async Task CreateOrder_UnknownArticle_400()
    {
        using var client = TenantClient();
        var response = await client.PostAsJsonAsync("/orders", new
        {
            id = Guid.CreateVersion7(),
            warehouse_id = _warehouseId,
            article_id = Guid.CreateVersion7(),
            qty_base = "1"
        }, Json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("error").GetString().ShouldBe("unknown_article");
        (await CountOrders()).ShouldBe(0);
    }

    [Fact]
    public async Task CreateOrder_HappyPath_PickTaskFromFifoHu()
    {
        (await Apply("CreateLocationBatch", Bin(Guid.CreateVersion7(), "A-01-01-01"))).Outcome.ShouldBe("Applied");
        (await Apply("CreateLocationBatch", Bin(Guid.CreateVersion7(), "A-01-01-02"))).Outcome.ShouldBe("Applied");

        var first = await PutawayHu(
            "LPN-FIRST", DateTimeOffset.Parse("2026-09-19T10:00:00Z", CultureInfo.InvariantCulture));
        var second = await PutawayHu(
            "LPN-SECOND", DateTimeOffset.Parse("2026-09-19T11:00:00Z", CultureInfo.InvariantCulture));

        using var client = TenantClient();
        var orderId = Guid.CreateVersion7();
        var response = await client.PostAsJsonAsync("/orders", new
        {
            id = orderId,
            warehouse_id = _warehouseId,
            article_id = _articleId,
            qty_base = "1"
        }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<OrderDto>(Json);
        dto.ShouldNotBeNull();
        dto.Id.ShouldBe(orderId);
        dto.Status.ShouldBe("allocated");
        dto.Source.ShouldBe("manual");
        var line = dto.Lines.ShouldHaveSingleItem();
        decimal.Parse(line.AllocatedQtyBase, CultureInfo.InvariantCulture).ShouldBe(1m);
        line.Status.ShouldBe("open");

        await using var db = OpenDb();
        var task = await db.Tasks.SingleAsync(t => t.Type == "pick");
        task.Status.ShouldBe("open");
        task.SuggestedLocationId.ShouldBe(first.BinId);
        var taskLine = await db.TaskLines.SingleAsync(l => l.TaskId == task.Id);
        taskLine.FromHandlingUnitId.ShouldBe(first.HuId);
        taskLine.FromLocationId.ShouldBe(first.BinId);
        var reserved = await db.StockBalances.SingleAsync(b => b.HandlingUnitId == first.HuId);
        reserved.ReservedQtyBase.ShouldBe(1m);
        var other = await db.StockBalances.SingleAsync(b => b.HandlingUnitId == second.HuId);
        other.ReservedQtyBase.ShouldBe(0m);
        (await db.ChangeLog.CountAsync(c => c.Entity == "order" && c.Id == orderId)).ShouldBe(1);
        (await db.ChangeLog.CountAsync(c => c.Entity == "task" && c.Id == task.Id)).ShouldBe(1);

        var list = await client.GetAsync($"/orders?warehouse={_warehouseId}");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = await list.Content.ReadFromJsonAsync<List<OrderDto>>(Json);
        listed.ShouldNotBeNull();
        listed.ShouldHaveSingleItem().Id.ShouldBe(orderId);
    }

    [Fact]
    public async Task CreateOrder_Shortage_LineShort()
    {
        using var client = TenantClient();
        var orderId = Guid.CreateVersion7();
        var response = await client.PostAsJsonAsync("/orders", new
        {
            id = orderId,
            warehouse_id = _warehouseId,
            article_id = _articleId,
            qty_base = "1"
        }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<OrderDto>(Json);
        dto.ShouldNotBeNull();
        dto.Status.ShouldBe("released");
        dto.Lines.ShouldHaveSingleItem().Status.ShouldBe("short");
        await using var db = OpenDb();
        (await db.Tasks.CountAsync(t => t.Type == "pick")).ShouldBe(0);
    }

    [Fact]
    public async Task CreateOrder_DuplicateExternalRef_409()
    {
        using var client = TenantClient();
        (await client.PostAsJsonAsync("/orders", new
        {
            id = Guid.CreateVersion7(),
            warehouse_id = _warehouseId,
            article_id = _articleId,
            qty_base = "1",
            external_ref = "ORD-DEMO"
        }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var duplicate = await client.PostAsJsonAsync("/orders", new
        {
            id = Guid.CreateVersion7(),
            warehouse_id = _warehouseId,
            article_id = _articleId,
            qty_base = "1",
            external_ref = "ORD-DEMO"
        }, Json);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var err = await duplicate.Content.ReadFromJsonAsync<JsonElement>(Json);
        err.GetProperty("error").GetString().ShouldBe("duplicate_external_ref");
        (await CountOrders()).ShouldBe(1);
    }

    private async Task<(Guid HuId, Guid BinId)> PutawayHu(string lpn, DateTimeOffset at)
    {
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", new
        {
            warehouse_id = _warehouseId,
            handling_unit_id = huId,
            task_id = taskId,
            lpn,
            article_id = _articleId,
            qty_base = "1"
        }, at)).Outcome.ShouldBe("Applied");
        await using (var db = OpenDb())
        {
            var suggested = (await db.Tasks.SingleAsync(t => t.Id == taskId)).SuggestedLocationId;
            suggested.ShouldNotBeNull();
            (await Apply("ConfirmPutaway", new
            {
                task_id = taskId,
                location_id = suggested!.Value,
                qty = "1"
            }, at)).Outcome.ShouldBe("Applied");
            return (huId, suggested.Value);
        }
    }

    private object Bin(Guid id, string code) => new
    {
        warehouse_id = _warehouseId,
        parent_id = (Guid?)null,
        type = "bin",
        locations = new[] { new { id, code, parent_id = (Guid?)null } }
    };

    private async Task<CommandResultDto> Apply(string type, object payload, DateTimeOffset? occurredAt = null)
    {
        using var client = TenantClient();
        var at = occurredAt ?? _clock.UtcNow;
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            new
            {
                tenant_id = _tenantId,
                now = at,
                commands = new[]
                {
                    new
                    {
                        id = Guid.CreateVersion7(),
                        type,
                        v = 1,
                        payload = JsonSerializer.SerializeToElement(payload, Json),
                        occurred_at = at,
                        device_id = _deviceId,
                        user_id = _workerId
                    }
                }
            },
            Json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        return body!.Results[0];
    }

    private TenantDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new TenantDbContext(options);
    }

    private sealed record BatchResultDto(List<CommandResultDto> Results);
    private sealed record CommandResultDto(
        Guid CommandId,
        string Outcome,
        string? Code,
        string? Message,
        JsonElement Details);

    private async Task<int> CountOrders()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from outbound_order", conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
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
