using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Jobs;
using Lagerkraft.WmsCore.Api.Layout;
using Lagerkraft.WmsCore.Catalog.Contracts;
using Lagerkraft.WmsCore.Layout.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class M1aTests : IAsyncLifetime
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
    private Guid _deviceId;
    private Guid _warehouseId;
    private Guid _articleId;
    private string _cs = "";

    public M1aTests(PostgresFixture postgres) => _postgres = postgres;

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
            new MembershipAssignment(_workerId, "floor_worker", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null),
            new MembershipAssignment(_workerId, "warehouse_manager", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null)
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
    public async Task ConfirmPutaway_OccupiedBin_ScopedFeedAndDisplace()
    {
        var binId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(binId, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        var existingHu = Guid.CreateVersion7();
        var existingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(existingHu, existingTask, "LPN-OLD"))).Outcome.ShouldBe("Applied");
        (await Apply("ConfirmPutaway", new { task_id = existingTask, location_id = binId, qty = "1" }))
            .Outcome.ShouldBe("Applied");

        var incomingHu = Guid.CreateVersion7();
        var incomingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(incomingHu, incomingTask, "LPN-NEW"))).Outcome.ShouldBe("Applied");
        var commandId = Guid.CreateVersion7();
        var result = await Apply(
            "ConfirmPutaway",
            new { task_id = incomingTask, location_id = binId, qty = "1" },
            commandId: commandId);
        result.Outcome.ShouldBe("Applied");

        await using var db = OpenDb();
        db.ChangeTracker.Clear();
        var unlocated = await db.Locations.SingleAsync(
            l => l.WarehouseId == _warehouseId && l.Code == LayoutDefaults.Unlocated);
        var existingBal = await db.StockBalances.SingleAsync(b => b.HandlingUnitId == existingHu);
        existingBal.LocationId.ShouldBe(unlocated.Id);
        existingBal.QtyBase.ShouldBe(1m);
        var incomingBal = await db.StockBalances.SingleAsync(b => b.HandlingUnitId == incomingHu);
        incomingBal.LocationId.ShouldBe(binId);
        (await db.StockMovements.CountAsync(m => m.CommandId == commandId && m.Reason == "displaced")).ShouldBe(1);
        (await db.StockMovements.CountAsync(m => m.CommandId == commandId && m.Reason == "putaway")).ShouldBe(1);
        (await db.Outbox.CountAsync(o => o.Type == "inventory.deviation.reported")).ShouldBe(1);

        var entries = await ScopedEntriesAsync();
        var commandRows = entries.Where(e =>
            e.TryGetProperty("command_id", out var cid)
            && cid.ValueKind != JsonValueKind.Null
            && cid.GetGuid() == commandId).ToList();
        foreach (var row in commandRows)
        {
            row.GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);
        }

        commandRows.Count(e => e.GetProperty("entity").GetString() == "handling_unit"
            && e.GetProperty("op").GetString() == "upsert").ShouldBe(1);
        commandRows.Single(e => e.GetProperty("entity").GetString() == "handling_unit")
            .GetProperty("id").GetGuid().ShouldBe(incomingHu);
        commandRows.Count(e => e.GetProperty("entity").GetString() == "stock_balance"
            && e.GetProperty("op").GetString() == "upsert").ShouldBe(2);
        commandRows.ShouldContain(e =>
            e.GetProperty("entity").GetString() == "stock_balance"
            && e.GetProperty("id").GetGuid() == StockBalanceId.For(incomingHu, _articleId));
        commandRows.ShouldContain(e =>
            e.GetProperty("entity").GetString() == "stock_balance"
            && e.GetProperty("id").GetGuid() == StockBalanceId.For(existingHu, _articleId));
        var deviation = commandRows.Single(e => e.GetProperty("entity").GetString() == "deviation");
        deviation.GetProperty("op").GetString().ShouldBe("upsert");
        deviation.GetProperty("payload").GetProperty("kind").GetString().ShouldBe("occupied_bin");
        deviation.GetProperty("payload").GetProperty("status").GetString().ShouldBe("open");
        var detail = deviation.GetProperty("payload").GetProperty("detail");
        detail.GetProperty("existing_handling_unit_id").GetGuid().ShouldBe(existingHu);
        detail.GetProperty("existing_reserved_qty_base").GetString().ShouldBe("0");
        detail.TryGetProperty("displaced_reserved_qty_base", out _).ShouldBeFalse();
        (await db.Deviations.SingleAsync(d => d.CommandId == commandId)).Status.ShouldBe("open");
        commandRows.Count(e => e.GetProperty("entity").GetString() == "task").ShouldBe(1);
        commandRows.ShouldNotContain(e => e.GetProperty("entity").GetString() == "stock_movement");
    }

    [Fact]
    public async Task ConfirmPutaway_OccupiedBin_DisplacedReservationCarriedToUnlocated()
    {
        var binId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(binId, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        var existingHu = Guid.CreateVersion7();
        var existingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(existingHu, existingTask, "LPN-RSV"))).Outcome.ShouldBe("Applied");
        (await Apply("ConfirmPutaway", new { task_id = existingTask, location_id = binId, qty = "1" }))
            .Outcome.ShouldBe("Applied");

        await using (var db = OpenDb())
        {
            var occupant = await db.StockBalances.SingleAsync(b => b.HandlingUnitId == existingHu);
            occupant.ReservedQtyBase = 1m;
            await db.SaveChangesAsync();
        }

        var incomingHu = Guid.CreateVersion7();
        var incomingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(incomingHu, incomingTask, "LPN-IN"))).Outcome.ShouldBe("Applied");
        var commandId = Guid.CreateVersion7();
        (await Apply("ConfirmPutaway", new { task_id = incomingTask, location_id = binId, qty = "1" }, commandId: commandId))
            .Outcome.ShouldBe("Applied");

        await using var after = OpenDb();
        var unlocated = await after.Locations.SingleAsync(
            l => l.WarehouseId == _warehouseId && l.Code == LayoutDefaults.Unlocated);
        var moved = await after.StockBalances.SingleAsync(b => b.HandlingUnitId == existingHu);
        moved.LocationId.ShouldBe(unlocated.Id);
        moved.ReservedQtyBase.ShouldBe(1m);
        var detail = JsonDocument.Parse(
            (await after.Deviations.SingleAsync(d => d.CommandId == commandId)).Detail).RootElement;
        detail.GetProperty("existing_handling_unit_id").GetGuid().ShouldBe(existingHu);
        detail.GetProperty("existing_reserved_qty_base").GetString().ShouldBe("1");
        detail.TryGetProperty("displaced_reserved_qty_base", out _).ShouldBeFalse();
        var entries = await ScopedEntriesAsync();
        var displaced = entries.Single(e =>
            e.GetProperty("entity").GetString() == "stock_balance"
            && e.GetProperty("id").GetGuid() == StockBalanceId.For(existingHu, _articleId)
            && e.TryGetProperty("command_id", out var cid)
            && cid.GetGuid() == commandId);
        displaced.GetProperty("payload").GetProperty("reserved_qty_base").GetString().ShouldBe("1");
    }

    [Fact]
    public async Task ConfirmPutaway_OccupiedBin_ReplaySameCommandId_Dedupe()
    {
        var binId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(binId, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        var existingHu = Guid.CreateVersion7();
        var existingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(existingHu, existingTask, "LPN-OLD"))).Outcome.ShouldBe("Applied");
        (await Apply("ConfirmPutaway", new { task_id = existingTask, location_id = binId, qty = "1" }))
            .Outcome.ShouldBe("Applied");

        var incomingHu = Guid.CreateVersion7();
        var incomingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(incomingHu, incomingTask, "LPN-NEW"))).Outcome.ShouldBe("Applied");
        var commandId = Guid.CreateVersion7();
        var payload = new { task_id = incomingTask, location_id = binId, qty = "1" };
        (await Apply("ConfirmPutaway", payload, commandId: commandId)).Outcome.ShouldBe("Applied");

        await using var before = OpenDb();
        var deviationCount = await before.Deviations.CountAsync(d => d.Kind == "occupied_bin");
        var changeCount = await before.ChangeLog.CountAsync(c => c.CommandId == commandId);
        var movementCount = await before.StockMovements.CountAsync(m => m.CommandId == commandId);
        deviationCount.ShouldBe(1);
        changeCount.ShouldBeGreaterThan(0);
        movementCount.ShouldBe(2);

        var replay = await Apply("ConfirmPutaway", payload, commandId: commandId);
        replay.Outcome.ShouldBe("Applied");

        await using var after = OpenDb();
        (await after.Deviations.CountAsync(d => d.Kind == "occupied_bin")).ShouldBe(1);
        (await after.ChangeLog.CountAsync(c => c.CommandId == commandId)).ShouldBe(changeCount);
        (await after.StockMovements.CountAsync(m => m.CommandId == commandId)).ShouldBe(movementCount);
    }

    [Fact]
    public async Task ConfirmPutaway_OccupiedBin_MixedPalletMovesBothArticles()
    {
        var binId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(binId, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        var existingHu = Guid.CreateVersion7();
        var existingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(existingHu, existingTask, "LPN-MIX"))).Outcome.ShouldBe("Applied");
        (await Apply("ConfirmPutaway", new { task_id = existingTask, location_id = binId, qty = "1" }))
            .Outcome.ShouldBe("Applied");

        var article2 = Guid.CreateVersion7();
        using (var client = InternalClient())
        {
            client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
            (await client.PostAsJsonAsync("/articles", new
            {
                id = article2,
                sku = "TE-100",
                name = "Te",
                base_uom_id = CatalogDefaults.StId
            }, Json)).EnsureSuccessStatusCode();
        }

        await using (var db = OpenDb())
        {
            var occupant = await db.StockBalances.SingleAsync(b => b.HandlingUnitId == existingHu);
            var level = await db.PackagingLevels.FirstAsync(p => p.ArticleId == article2);
            db.HandlingUnitContents.Add(new HandlingUnitContent
            {
                Id = Ids.New(),
                HandlingUnitId = existingHu,
                ArticleId = article2,
                QtyBase = 2m,
                PackagingLevelId = level.Id
            });
            db.StockBalances.Add(new StockBalance
            {
                LocationId = occupant.LocationId,
                ArticleId = article2,
                HandlingUnitId = existingHu,
                QtyBase = 2m,
                ReservedQtyBase = 0
            });
            await db.SaveChangesAsync();
        }

        var incomingHu = Guid.CreateVersion7();
        var incomingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(incomingHu, incomingTask, "LPN-IN"))).Outcome.ShouldBe("Applied");
        var commandId = Guid.CreateVersion7();
        (await Apply("ConfirmPutaway", new { task_id = incomingTask, location_id = binId, qty = "1" }, commandId: commandId))
            .Outcome.ShouldBe("Applied");

        await using var after = OpenDb();
        after.ChangeTracker.Clear();
        var unlocated = await after.Locations.SingleAsync(
            l => l.WarehouseId == _warehouseId && l.Code == LayoutDefaults.Unlocated);
        var moved = await after.StockBalances.Where(b => b.HandlingUnitId == existingHu).ToListAsync();
        moved.Count.ShouldBe(2);
        moved.ShouldAllBe(b => b.LocationId == unlocated.Id);
        moved.Select(b => b.ArticleId).ShouldBe([_articleId, article2], ignoreOrder: true);
        (await after.StockMovements.CountAsync(m => m.CommandId == commandId && m.Reason == "displaced")).ShouldBe(2);

        var entries = await ScopedEntriesAsync();
        var balances = entries.Where(e =>
            e.GetProperty("entity").GetString() == "stock_balance"
            && e.TryGetProperty("command_id", out var cid)
            && cid.GetGuid() == commandId).ToList();
        balances.Count.ShouldBe(3);
        balances.ShouldContain(e => e.GetProperty("id").GetGuid() == StockBalanceId.For(existingHu, _articleId));
        balances.ShouldContain(e => e.GetProperty("id").GetGuid() == StockBalanceId.For(existingHu, article2));
        balances.ShouldContain(e => e.GetProperty("id").GetGuid() == StockBalanceId.For(incomingHu, _articleId));
    }

    [Fact]
    public async Task ConfirmPutaway_WrongLocation_ScopedFeed()
    {
        var suggested = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(suggested, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        (await Apply("CreateLocationBatch", Bin(other, "A-01-01-02"))).Outcome.ShouldBe("Applied");
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(huId, taskId, "LPN-WRG"))).Outcome.ShouldBe("Applied");
        var commandId = Guid.CreateVersion7();
        var result = await Apply(
            "ConfirmPutaway",
            new { task_id = taskId, location_id = other, qty = "1" },
            commandId: commandId);
        result.Outcome.ShouldBe("Applied");

        var entries = await ScopedEntriesAsync();
        var commandRows = entries.Where(e =>
            e.TryGetProperty("command_id", out var cid)
            && cid.ValueKind != JsonValueKind.Null
            && cid.GetGuid() == commandId).ToList();
        commandRows.ShouldAllBe(e => e.GetProperty("warehouse_id").GetGuid() == _warehouseId);
        commandRows.Count(e => e.GetProperty("entity").GetString() == "handling_unit").ShouldBe(1);
        commandRows.Count(e => e.GetProperty("entity").GetString() == "stock_balance").ShouldBe(1);
        commandRows.Count(e => e.GetProperty("entity").GetString() == "task").ShouldBe(1);
        var deviation = commandRows.Single(e => e.GetProperty("entity").GetString() == "deviation");
        deviation.GetProperty("op").GetString().ShouldBe("upsert");
        deviation.GetProperty("payload").GetProperty("kind").GetString().ShouldBe("wrong_location");
        deviation.GetProperty("payload").GetProperty("status").GetString().ShouldBe("open");
        deviation.GetProperty("payload").GetProperty("detail").GetProperty("suggested_location_id").GetGuid()
            .ShouldBe(suggested);
        deviation.GetProperty("payload").GetProperty("detail").GetProperty("location_id").GetGuid()
            .ShouldBe(other);
    }

    [Fact]
    public async Task ConfirmPutaway_Unlocated_Rejected()
    {
        var destId = await SystemLocationIdAsync(LayoutDefaults.Unlocated);
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(huId, taskId, "LPN-UNL"))).Outcome.ShouldBe("Applied");
        var result = await Apply("ConfirmPutaway", new { task_id = taskId, location_id = destId, qty = "1" });
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("unknown_location");
        await using var db = OpenDb();
        (await db.Tasks.SingleAsync(t => t.Id == taskId)).Status.ShouldBe("open");
        (await db.StockBalances.SingleAsync(b => b.HandlingUnitId == huId)).LocationId
            .ShouldNotBe(destId);
    }

    [Fact]
    public async Task ConfirmPutaway_Adjustment_Rejected()
    {
        var destId = await SystemLocationIdAsync(LayoutDefaults.Adjustment);
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(huId, taskId, "LPN-ADJ"))).Outcome.ShouldBe("Applied");
        var result = await Apply("ConfirmPutaway", new { task_id = taskId, location_id = destId, qty = "1" });
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("unknown_location");
        await using var db = OpenDb();
        (await db.Tasks.SingleAsync(t => t.Id == taskId)).Status.ShouldBe("open");
        (await db.StockBalances.SingleAsync(b => b.HandlingUnitId == huId)).LocationId
            .ShouldNotBe(destId);
    }

    [Fact]
    public async Task ConfirmPutaway_Quarantine_Accepted()
    {
        var destId = await SystemLocationIdAsync(LayoutDefaults.Quarantine);
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(huId, taskId, "LPN-QUA"))).Outcome.ShouldBe("Applied");
        var result = await Apply("ConfirmPutaway", new { task_id = taskId, location_id = destId, qty = "1" });
        result.Outcome.ShouldBe("Applied");
        await using var db = OpenDb();
        (await db.Tasks.SingleAsync(t => t.Id == taskId)).Status.ShouldBe("done");
        (await db.StockBalances.SingleAsync(b => b.HandlingUnitId == huId)).LocationId.ShouldBe(destId);
    }

    [Fact]
    public async Task Changes_WarehouseScope_IncludesNullExcludesOtherWarehouse()
    {
        var otherWh = Guid.CreateVersion7();
        using (var client = InternalClient())
        {
            client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
            (await client.PostAsJsonAsync("/warehouses", new { id = otherWh, name = "WH2" }, Json))
                .EnsureSuccessStatusCode();
        }

        (await Apply("CreateLocationBatch", Bin(Guid.CreateVersion7(), "A-01-01-01"))).Outcome.ShouldBe("Applied");
        await using (var db = OpenDb())
        {
            db.ChangeLog.Add(new ChangeLogRow
            {
                Entity = "location",
                Id = Guid.CreateVersion7(),
                Op = "upsert",
                Payload = "{}",
                WarehouseId = otherWh,
                OccurredAt = _clock.UtcNow,
                RecordedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var entries = await ScopedEntriesAsync();
        entries.ShouldNotBeEmpty();
        entries.ShouldContain(e =>
            e.GetProperty("entity").GetString() == "article"
            && e.GetProperty("warehouse_id").ValueKind == JsonValueKind.Null);
        foreach (var entry in entries)
        {
            if (entry.GetProperty("warehouse_id").ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            entry.GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);
        }
    }

    [Fact]
    public async Task Probe_DoesNotWriteChangeLog()
    {
        var probeId = Guid.CreateVersion7();
        var result = await Apply("Probe", new { probe_id = probeId, message = "hello" });
        result.Outcome.ShouldBe("Applied");
        await using var db = OpenDb();
        (await db.ChangeLog.CountAsync(c => c.Entity == "probe")).ShouldBe(0);
        (await db.Outbox.CountAsync(o => o.Type == "inventory.probe.applied")).ShouldBe(1);
    }

    [Fact]
    public async Task Changes_SinceZeroOnFreshTenant_Ok()
    {
        var fresh = Guid.CreateVersion7();
        var cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, fresh);
        _factory.Platform.Connections[fresh] = cs;
        using var client = InternalClient();
        (await client.PostAsync($"/internal/tenants/{fresh}/migrate", null)).EnsureSuccessStatusCode();
        var response = await client.GetAsync($"/internal/changes?tenantId={fresh}&since=0");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = OpenDb(cs);
        (await db.ChangeLog.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Changes_RealGap_410()
    {
        (await Apply("CreateLocationBatch", Bin(Guid.CreateVersion7(), "A-01-01-01"))).Outcome.ShouldBe("Applied");
        await using var db = OpenDb();
        var min = await db.ChangeLog.MinAsync(c => c.Seq);
        var first = await db.ChangeLog.SingleAsync(c => c.Seq == min);
        db.ChangeLog.Remove(first);
        await db.SaveChangesAsync();

        using var client = InternalClient();
        var gone = await client.GetAsync($"/internal/changes?tenantId={_tenantId}&since=0");
        gone.StatusCode.ShouldBe(HttpStatusCode.Gone);
        var ok = await client.GetAsync($"/internal/changes?tenantId={_tenantId}&since={min}");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changes_PrunedEmpty_410()
    {
        (await Apply("CreateLocationBatch", Bin(Guid.CreateVersion7(), "A-01-01-01"))).Outcome.ShouldBe("Applied");
        await using var db = OpenDb();
        db.ChangeLog.RemoveRange(db.ChangeLog);
        await db.SaveChangesAsync();

        using var client = InternalClient();
        var gone = await client.GetAsync($"/internal/changes?tenantId={_tenantId}&since=0");
        gone.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Snapshot_IncludesSeqAndDeviationAndBalanceId()
    {
        var suggested = Guid.CreateVersion7();
        var actual = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(suggested, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        (await Apply("CreateLocationBatch", Bin(actual, "A-01-01-02"))).Outcome.ShouldBe("Applied");
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(huId, taskId, "LPN-SNAP"))).Outcome.ShouldBe("Applied");
        (await Apply("ConfirmPutaway", new { task_id = taskId, location_id = actual, qty = "1" }))
            .Outcome.ShouldBe("Applied");

        using var client = InternalClient();
        var snap = await client.GetAsync(
            $"/internal/snapshot?tenantId={_tenantId}&warehouse={_warehouseId}&entity=StockBalance");
        snap.EnsureSuccessStatusCode();
        var body = await snap.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.TryGetProperty("seq", out _).ShouldBeTrue();
        var item = body.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("id").GetGuid().ShouldBe(StockBalanceId.For(huId, _articleId));

        await using var db = OpenDb();
        var deviation = await db.Deviations.SingleAsync(d => d.Kind == "wrong_location");
        deviation.Status.ShouldBe("open");
        var maxSeq = await db.ChangeLog.MaxAsync(c => c.Seq);
        body.GetProperty("seq").GetInt64().ShouldBe(maxSeq);

        var pendingId = Guid.CreateVersion7();
        var resolvedId = Guid.CreateVersion7();
        var dismissedId = Guid.CreateVersion7();
        db.Deviations.AddRange(
            new Deviation
            {
                Id = pendingId,
                WarehouseId = _warehouseId,
                Kind = "stale_command",
                Status = "pending_approval",
                Detail = "{}",
                CreatedAt = _clock.UtcNow
            },
            new Deviation
            {
                Id = resolvedId,
                WarehouseId = _warehouseId,
                Kind = "reported",
                Status = "resolved",
                Detail = "{}",
                CreatedAt = _clock.UtcNow
            },
            new Deviation
            {
                Id = dismissedId,
                WarehouseId = _warehouseId,
                Kind = "reported",
                Status = "dismissed",
                Detail = "{}",
                CreatedAt = _clock.UtcNow
            });
        await db.SaveChangesAsync();

        var devSnap = await client.GetAsync(
            $"/internal/snapshot?tenantId={_tenantId}&warehouse={_warehouseId}&entity=deviation");
        devSnap.EnsureSuccessStatusCode();
        var devBody = await devSnap.Content.ReadFromJsonAsync<JsonElement>(Json);
        var ids = devBody.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();
        ids.ShouldContain(deviation.Id);
        ids.ShouldContain(pendingId);
        ids.ShouldNotContain(resolvedId);
        ids.ShouldNotContain(dismissedId);
        var openItem = devBody.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == deviation.Id);
        openItem.GetProperty("kind").GetString().ShouldBe("wrong_location");
        openItem.GetProperty("status").GetString().ShouldBe("open");
    }

    [Fact]
    public async Task SystemLocationEnsure_AddsUnlocatedOnce_ThenOccupiedPutaway()
    {
        await using (var db = OpenDb())
        {
            var existing = await db.Locations.SingleAsync(
                l => l.WarehouseId == _warehouseId && l.Code == LayoutDefaults.Unlocated);
            db.Locations.Remove(existing);
            var logs = await db.ChangeLog.Where(c => c.Id == existing.Id).ToListAsync();
            db.ChangeLog.RemoveRange(logs);
            await db.SaveChangesAsync();

            var added = await SystemLocationEnsure.RunAsync(db, _clock.UtcNow, CancellationToken.None);
            added.ShouldBe(1);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var row = await db.Locations.SingleAsync(
                l => l.WarehouseId == _warehouseId && l.Code == LayoutDefaults.Unlocated);
            var change = await db.ChangeLog.SingleAsync(c => c.Entity == "location" && c.Id == row.Id);
            change.WarehouseId.ShouldBe(_warehouseId);
            change.CommandId.ShouldBeNull();
            change.Actor.ShouldBeNull();
            change.Op.ShouldBe("upsert");

            var second = await SystemLocationEnsure.RunAsync(db, _clock.UtcNow, CancellationToken.None);
            second.ShouldBe(0);
        }

        var binId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(binId, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        var existingHu = Guid.CreateVersion7();
        var existingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(existingHu, existingTask, "LPN-OLD"))).Outcome.ShouldBe("Applied");
        (await Apply("ConfirmPutaway", new { task_id = existingTask, location_id = binId, qty = "1" }))
            .Outcome.ShouldBe("Applied");
        var incomingHu = Guid.CreateVersion7();
        var incomingTask = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(incomingHu, incomingTask, "LPN-NEW"))).Outcome.ShouldBe("Applied");
        (await Apply("ConfirmPutaway", new { task_id = incomingTask, location_id = binId, qty = "1" }))
            .Outcome.ShouldBe("Applied");

        await using var after = OpenDb();
        var unlocated = await after.Locations.SingleAsync(
            l => l.WarehouseId == _warehouseId && l.Code == LayoutDefaults.Unlocated);
        (await after.StockBalances.SingleAsync(b => b.HandlingUnitId == existingHu)).LocationId.ShouldBe(unlocated.Id);
        (await after.Deviations.CountAsync(d => d.Kind == "occupied_bin")).ShouldBe(1);
    }

    [Fact]
    public async Task AssignmentSweep_WaitsForTenantLock()
    {
        var binId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(binId, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(huId, taskId, "LPN-LCK"))).Outcome.ShouldBe("Applied");
        (await Apply("ClaimTask", new { task_id = taskId })).Outcome.ShouldBe("Applied");
        _clock.Advance(TimeSpan.FromMinutes(31));

        await using var holder = OpenDb();
        await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            CommandDispatcher.LockKey(_tenantId));

        var sweep = _factory.Services.GetRequiredService<AssignmentSweepJob>();
        var sweepTask = sweep.SweepTenantAsync(_tenantId, CancellationToken.None);
        var delay = Task.Delay(TimeSpan.FromMilliseconds(500));
        (await Task.WhenAny(sweepTask, delay)).ShouldBe(delay);
        sweepTask.IsCompleted.ShouldBeFalse();

        await tx.RollbackAsync();
        await sweepTask.WaitAsync(TimeSpan.FromSeconds(5));

        await using var db = OpenDb();
        (await db.Tasks.SingleAsync(t => t.Id == taskId)).Status.ShouldBe("open");
    }

    [Fact]
    public async Task AssignmentSweep_ReleasesExpiredClaim_SetsWarehouseId()
    {
        var binId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(binId, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(huId, taskId, "LPN-SWP"))).Outcome.ShouldBe("Applied");
        (await Apply("ClaimTask", new { task_id = taskId })).Outcome.ShouldBe("Applied");

        _clock.Advance(TimeSpan.FromMinutes(31));
        var sweep = _factory.Services.GetRequiredService<AssignmentSweepJob>();
        await sweep.SweepTenantAsync(_tenantId, CancellationToken.None);

        await using var db = OpenDb();
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId);
        task.Status.ShouldBe("open");
        task.AssigneeUserId.ShouldBeNull();
        var rows = await db.ChangeLog.Where(c => c.Entity == "task" && c.Id == taskId).ToListAsync();
        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(r => r.WarehouseId == _warehouseId);
    }

    [Fact]
    public async Task ReservationSweep_DeletesExpiredKeepsLive()
    {
        var liveBin = Guid.CreateVersion7();
        var expiredBin = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(liveBin, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        (await Apply("CreateLocationBatch", Bin(expiredBin, "A-01-01-02"))).Outcome.ShouldBe("Applied");
        await using (var db = OpenDb())
        {
            db.LocationReservations.Add(new LocationReservation
            {
                LocationId = liveBin,
                TaskId = Guid.CreateVersion7(),
                ExpiresAt = _clock.UtcNow.AddMinutes(10)
            });
            db.LocationReservations.Add(new LocationReservation
            {
                LocationId = expiredBin,
                TaskId = Guid.CreateVersion7(),
                ExpiresAt = _clock.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        var sweep = _factory.Services.GetRequiredService<LocationReservationSweepJob>();
        await sweep.SweepTenantAsync(_tenantId, CancellationToken.None);

        await using var after = OpenDb();
        (await after.LocationReservations.CountAsync()).ShouldBe(1);
        (await after.LocationReservations.SingleAsync()).LocationId.ShouldBe(liveBin);
    }

    [Fact]
    public async Task ReservationSweep_WaitsForTenantLock()
    {
        var expiredBin = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(expiredBin, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        await using (var db = OpenDb())
        {
            db.LocationReservations.Add(new LocationReservation
            {
                LocationId = expiredBin,
                TaskId = Guid.CreateVersion7(),
                ExpiresAt = _clock.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        await using var holder = OpenDb();
        await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            CommandDispatcher.LockKey(_tenantId));

        var sweep = _factory.Services.GetRequiredService<LocationReservationSweepJob>();
        var sweepTask = sweep.SweepTenantAsync(_tenantId, CancellationToken.None);
        var delay = Task.Delay(TimeSpan.FromMilliseconds(500));
        (await Task.WhenAny(sweepTask, delay)).ShouldBe(delay);
        sweepTask.IsCompleted.ShouldBeFalse();

        await tx.RollbackAsync();
        await sweepTask.WaitAsync(TimeSpan.FromSeconds(5));

        await using var after = OpenDb();
        (await after.LocationReservations.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ChangeLogWriters_WarehouseScopedHaveWarehouseId()
    {
        var binId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(binId, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        (await Apply("SetLocationDimensions", new
        {
            ids = new[] { binId },
            height_mm = 1200,
            width_mm = 800,
            depth_mm = 400,
            max_weight_g = 500000
        })).Outcome.ShouldBe("Applied");

        var huId = Guid.CreateVersion7();
        var putawayId = Guid.CreateVersion7();
        (await Apply("ReceiveHandlingUnit", ReceivePayload(huId, putawayId, "LPN-GRD"))).Outcome.ShouldBe("Applied");
        var claim = await Apply("ClaimTask", new { task_id = putawayId });
        claim.Outcome.ShouldBe("Applied");
        (await Apply("ReleaseTask", new { task_id = putawayId })).Outcome.ShouldBe("Applied");
        (await Apply("ConfirmPutaway", new { task_id = putawayId, location_id = binId, qty = "1" }))
            .Outcome.ShouldBe("Applied");

        var orderId = Guid.CreateVersion7();
        using (var client = InternalClient())
        {
            client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
            (await client.PostAsJsonAsync("/orders", new
            {
                id = orderId,
                warehouse_id = _warehouseId,
                article_id = _articleId,
                qty_base = "1"
            }, Json)).EnsureSuccessStatusCode();
        }

        await using var db = OpenDb();
        var pickTask = await db.Tasks.SingleAsync(t => t.Type == "pick" && t.WarehouseId == _warehouseId);
        var toteId = Guid.CreateVersion7();
        (await Apply("ConfirmPick", new
        {
            task_id = pickTask.Id,
            tote_id = toteId,
            tote_lpn = "TOTE-GRD",
            qty = "1"
        })).Outcome.ShouldBe("Applied");
        (await Apply("ShipAsPicked", new { order_id = orderId, tote_id = toteId })).Outcome.ShouldBe("Applied");

        db.ChangeTracker.Clear();
        var rows = await db.ChangeLog.ToListAsync();
        rows.Where(r => r.Entity != "article").ShouldNotBeEmpty();
        rows.Where(r => r.Entity != "article").ShouldAllBe(r => r.WarehouseId != null);
        rows.Where(r => r.Entity == "article").ShouldAllBe(r => r.WarehouseId == null);
    }

    private async Task<Guid> SystemLocationIdAsync(string code)
    {
        await using var db = OpenDb();
        return (await db.Locations.SingleAsync(
            l => l.WarehouseId == _warehouseId && l.IsSystem && l.Code == code)).Id;
    }

    private async Task<List<JsonElement>> ScopedEntriesAsync()
    {
        using var client = InternalClient();
        var feed = await client.GetAsync($"/internal/changes?tenantId={_tenantId}&warehouse={_warehouseId}");
        feed.EnsureSuccessStatusCode();
        var body = await feed.Content.ReadFromJsonAsync<JsonDocument>(Json);
        return body!.RootElement.GetProperty("entries").EnumerateArray().ToList();
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
                        user_id = _workerId
                    }
                }
            },
            Json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        return body!.Results[0];
    }

    private TenantDbContext OpenDb(string? cs = null)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs ?? _cs)
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

    private sealed record BatchResultDto(List<CommandResultDto> Results);
    private sealed record CommandResultDto(Guid CommandId, string Outcome, string? Code, string? Message);
}
