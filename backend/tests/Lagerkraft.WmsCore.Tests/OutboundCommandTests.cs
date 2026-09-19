using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboundCommandTests : IAsyncLifetime
{
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
    private Guid _viewerId;
    private Guid _deviceId;
    private Guid _warehouseId;
    private Guid _articleId;
    private string _cs = "";

    public OutboundCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _workerId = Guid.CreateVersion7();
        _viewerId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _warehouseId = Guid.CreateVersion7();
        _articleId = Guid.CreateVersion7();
        _cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, _tenantId);
        _factory.Platform.ConnectionString = _cs;
        _factory.Platform.Entitlement = new(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.Memberships =
        [
            new MembershipAssignment(_workerId, "floor_worker", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null),
            new MembershipAssignment(_viewerId, "viewer", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null)
        ];
        _factory.Clock = _clock;
        using var client = InternalClient();
        (await client.PostAsync($"/internal/tenants/{_tenantId}/migrate", null)).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
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
    public async Task ConfirmPick_ShortPick_Rejected()
    {
        var seeded = await SeedAllocatedPickAsync();
        await using (var db = OpenDb())
        {
            db.StockBalances.RemoveRange(db.StockBalances);
            await db.SaveChangesAsync();
        }

        var result = await Apply("ConfirmPick", new
        {
            task_id = seeded.TaskId,
            tote_id = Guid.CreateVersion7(),
            tote_lpn = "TOTE-SHORT",
            qty = "1"
        });
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("short_pick");
        await using var again = OpenDb();
        (await again.Deviations.CountAsync(d => d.CommandId == result.CommandId && d.Kind == "short_pick"))
            .ShouldBe(1);
        (await again.StockMovements.CountAsync(m => m.Reason == "pick")).ShouldBe(0);
        var picking = await again.Locations.SingleAsync(l => l.WarehouseId == _warehouseId && l.Code == "PICKING");
        (await again.StockBalances.AnyAsync(b => b.LocationId == picking.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task ConfirmPick_HappyPath_ToteAtPicking()
    {
        var seeded = await SeedAllocatedPickAsync();
        var toteId = Guid.CreateVersion7();
        var result = await Apply("ConfirmPick", new
        {
            task_id = seeded.TaskId,
            tote_id = toteId,
            tote_lpn = "TOTE-" + toteId.ToString("N")[..8].ToUpperInvariant(),
            qty = "1"
        });
        result.Outcome.ShouldBe("Applied");

        await using var db = OpenDb();
        var picking = await db.Locations.SingleAsync(l => l.WarehouseId == _warehouseId && l.Code == "PICKING");
        (await db.StockBalances.AnyAsync(b => b.LocationId == seeded.BinId)).ShouldBeFalse();
        var dest = await db.StockBalances.SingleAsync();
        dest.LocationId.ShouldBe(picking.Id);
        dest.HandlingUnitId.ShouldBe(toteId);
        dest.QtyBase.ShouldBe(1m);
        dest.ReservedQtyBase.ShouldBe(0m);
        var move = await db.StockMovements.SingleAsync(m => m.Reason == "pick");
        move.FromLocationId.ShouldBe(seeded.BinId);
        move.ToLocationId.ShouldBe(picking.Id);
        move.FromHandlingUnitId.ShouldBe(seeded.HuId);
        move.ToHandlingUnitId.ShouldBe(toteId);
        (await db.Tasks.SingleAsync(t => t.Id == seeded.TaskId)).Status.ShouldBe("done");
        (await db.OutboundOrders.SingleAsync(o => o.Id == seeded.OrderId)).Status.ShouldBe("picked");
        (await db.OutboundOrderLines.SingleAsync(l => l.OrderId == seeded.OrderId)).Status.ShouldBe("picked");
        (await db.LocationReservations.CountAsync()).ShouldBe(0);
        (await db.HandlingUnits.AnyAsync(h => h.Id == toteId && h.WarehouseId == _warehouseId)).ShouldBeTrue();
    }

    [Fact]
    public async Task ConfirmPick_Viewer_Forbidden()
    {
        var seeded = await SeedAllocatedPickAsync();
        var toteId = Guid.CreateVersion7();
        var result = await Apply("ConfirmPick", new
        {
            task_id = seeded.TaskId,
            tote_id = toteId,
            tote_lpn = "TOTE-VIEW",
            qty = "1"
        }, userId: _viewerId);
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("forbidden");
        await using var db = OpenDb();
        (await db.Tasks.SingleAsync(t => t.Id == seeded.TaskId)).Status.ShouldBe("open");
        (await db.StockMovements.CountAsync(m => m.Reason == "pick")).ShouldBe(0);
    }

    [Fact]
    public async Task ConfirmPick_IdempotentRetry()
    {
        var seeded = await SeedAllocatedPickAsync();
        var toteId = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        var payload = new
        {
            task_id = seeded.TaskId,
            tote_id = toteId,
            tote_lpn = "TOTE-IDM",
            qty = "1"
        };
        var first = await Apply("ConfirmPick", payload, commandId: commandId);
        var second = await Apply("ConfirmPick", payload, commandId: commandId);
        first.Outcome.ShouldBe("Applied");
        second.Outcome.ShouldBe("Applied");
        second.CommandId.ShouldBe(commandId);
        await using var db = OpenDb();
        (await db.StockMovements.CountAsync(m => m.CommandId == commandId)).ShouldBe(1);
        (await db.HandlingUnits.CountAsync(h => h.Id == toteId)).ShouldBe(1);
    }

    [Fact]
    public async Task Snapshot_EntityOrder_ReturnsAllocatedStatus()
    {
        var seeded = await SeedAllocatedPickAsync();
        using var client = InternalClient();
        var response = await client.GetAsync(
            $"/internal/snapshot?tenantId={_tenantId}&warehouse={_warehouseId}&entity=Order");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("entity").GetString().ShouldBe("Order");
        var item = body.GetProperty("items").EnumerateArray().Single(el => el.GetProperty("id").GetGuid() == seeded.OrderId);
        item.GetProperty("status").GetString().ShouldBe("allocated");
        item.GetProperty("lines").EnumerateArray().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ShipAsPicked_OrderNotPicked_Rejected()
    {
        var seeded = await SeedAllocatedPickAsync();
        var result = await Apply("ShipAsPicked", new
        {
            order_id = seeded.OrderId,
            tote_id = Guid.CreateVersion7()
        });
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("invalid_status");
        await using var db = OpenDb();
        (await db.OutboundOrders.SingleAsync(o => o.Id == seeded.OrderId)).Status.ShouldBe("allocated");
        (await db.Shipments.CountAsync()).ShouldBe(0);
        (await db.StockMovements.CountAsync(m => m.Reason == "ship")).ShouldBe(0);
    }

    [Fact]
    public async Task ShipAsPicked_HappyPath_BalanceGone()
    {
        var seeded = await SeedPickedToteAsync();
        var result = await Apply("ShipAsPicked", new
        {
            order_id = seeded.OrderId,
            tote_id = seeded.ToteId
        });
        result.Outcome.ShouldBe("Applied");

        await using var db = OpenDb();
        var picking = await db.Locations.SingleAsync(l => l.WarehouseId == _warehouseId && l.Code == "PICKING");
        (await db.StockBalances.AnyAsync(b => b.LocationId == picking.Id)).ShouldBeFalse();
        (await db.OutboundOrders.SingleAsync(o => o.Id == seeded.OrderId)).Status.ShouldBe("shipped");
        var shipment = await db.Shipments.SingleAsync(s => s.OrderId == seeded.OrderId);
        (await db.ShipmentHandlingUnits.SingleAsync(h => h.ShipmentId == shipment.Id)).HandlingUnitId.ShouldBe(seeded.ToteId);
        var move = await db.StockMovements.SingleAsync(m => m.Reason == "ship");
        move.FromLocationId.ShouldBe(picking.Id);
        move.ToLocationId.ShouldBeNull();
        move.FromHandlingUnitId.ShouldBe(seeded.ToteId);
        move.ToHandlingUnitId.ShouldBeNull();
        (await db.Outbox.CountAsync(o => o.Type == "outbound.shipped")).ShouldBe(1);
    }

    [Fact]
    public async Task ShipAsPicked_IdempotentRetry()
    {
        var seeded = await SeedPickedToteAsync();
        var commandId = Guid.CreateVersion7();
        var payload = new { order_id = seeded.OrderId, tote_id = seeded.ToteId };
        var first = await Apply("ShipAsPicked", payload, commandId: commandId);
        var second = await Apply("ShipAsPicked", payload, commandId: commandId);
        first.Outcome.ShouldBe("Applied");
        second.Outcome.ShouldBe("Applied");
        second.CommandId.ShouldBe(commandId);
        await using var db = OpenDb();
        (await db.StockMovements.CountAsync(m => m.CommandId == commandId)).ShouldBe(1);
        (await db.Shipments.CountAsync(s => s.OrderId == seeded.OrderId)).ShouldBe(1);
    }

    private async Task<SeededPick> SeedAllocatedPickAsync()
    {
        var binId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(binId, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        var huId = Guid.CreateVersion7();
        var putawayId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(huId, putawayId, "LPN-PICK"))).Outcome.ShouldBe("Applied");
        (await Apply("ConfirmPutaway", new { task_id = putawayId, location_id = binId, qty = "1" }))
            .Outcome.ShouldBe("Applied");

        var orderId = Guid.CreateVersion7();
        using var client = TenantClient();
        var created = await client.PostAsJsonAsync("/orders", new
        {
            id = orderId,
            warehouse_id = _warehouseId,
            article_id = _articleId,
            qty_base = "1"
        }, Json);
        created.EnsureSuccessStatusCode();

        await using var db = OpenDb();
        var taskId = await db.Tasks.Where(t => t.Type == "pick" && t.WarehouseId == _warehouseId)
            .Select(t => t.Id)
            .SingleAsync();
        return new SeededPick(orderId, taskId, huId, binId);
    }

    private async Task<SeededPicked> SeedPickedToteAsync()
    {
        var seeded = await SeedAllocatedPickAsync();
        var toteId = Guid.CreateVersion7();
        (await Apply("ConfirmPick", new
        {
            task_id = seeded.TaskId,
            tote_id = toteId,
            tote_lpn = "TOTE-" + toteId.ToString("N")[..8].ToUpperInvariant(),
            qty = "1"
        })).Outcome.ShouldBe("Applied");
        return new SeededPicked(seeded.OrderId, toteId);
    }

    private object ReceivePayload(Guid huId, Guid taskId, string lpn) => new
    {
        warehouse_id = _warehouseId,
        handling_unit_id = huId,
        task_id = taskId,
        lpn,
        article_id = _articleId,
        qty_base = "1"
    };

    private object Bin(Guid id, string code) => new
    {
        warehouse_id = _warehouseId,
        parent_id = (Guid?)null,
        type = "bin",
        locations = new[] { new { id, code, parent_id = (Guid?)null } }
    };

    private async Task<CommandResultDto> Apply(
        string type,
        object payload,
        Guid? userId = null,
        Guid? commandId = null)
    {
        using var client = InternalClient();
        var at = _clock.UtcNow;
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
                        id = commandId ?? Guid.CreateVersion7(),
                        type,
                        v = 1,
                        payload = JsonSerializer.SerializeToElement(payload, Json),
                        occurred_at = at,
                        device_id = _deviceId,
                        user_id = userId ?? _workerId
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

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        return client;
    }

    private HttpClient TenantClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
        return client;
    }

    private sealed record SeededPick(Guid OrderId, Guid TaskId, Guid HuId, Guid BinId);
    private sealed record SeededPicked(Guid OrderId, Guid ToteId);
    private sealed record BatchResultDto(List<CommandResultDto> Results);
    private sealed record CommandResultDto(
        Guid CommandId,
        string Outcome,
        string? Code,
        string? Message,
        int? ClockSkewMs);
}
