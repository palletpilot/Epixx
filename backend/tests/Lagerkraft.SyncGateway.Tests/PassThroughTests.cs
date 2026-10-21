using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lagerkraft.SyncGateway.Clients;

namespace Lagerkraft.SyncGateway.Tests;

[Trait("Category", "Integration")]
public sealed class PassThroughTests : IAsyncLifetime
{
    // WmsCore PR 8 change_log writes one op. Occupied-bin deviation uses the same vocabulary.
    private const string DeviationChangeOp = "upsert";

    // Test-only: matches WmsCore PR 8 @ 500117c / spec PR 5 @ e3b2acf.
    // The gateway treats change row id as opaque and never computes this.
    private static readonly Guid StockBalanceChangeIdNamespace =
        Guid.Parse("9296f5d4-6bd8-44c5-b874-be71680d1d6b");

    private static readonly Guid PinnedHuId =
        Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid PinnedArticleId =
        Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid PinnedStockBalanceId =
        Guid.Parse("f4faf703-c1f7-5805-9e1f-aed95cc6831f");

    private const string SnapshotSchemaM1a = "20261021083504_M1aWarehouseScopedFeed";

    private readonly SyncGatewayApiFactory _factory = new();
    private Guid _tenantId;
    private Guid _userId;
    private Guid _deviceId;
    private Guid _warehouseId;
    // Warehouse UNLOCATED system location (is_system, code UNLOCATED). Real id, never null.
    private Guid _unlocatedLocationId;

    public Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _userId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _warehouseId = Guid.CreateVersion7();
        _unlocatedLocationId = Guid.CreateVersion7();
        _factory.Platform.Devices[_deviceId] = new InternalDevice(
            _deviceId, _tenantId, "floor-1", [_tenantId], null, 0);
        _factory.Platform.Entitlement = new TenantEntitlement(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.CurrentSessionVersion = 1;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public void StockBalanceChangeId_KnownPair_MatchesPinnedVector()
    {
        StockBalanceChangeId(PinnedHuId, PinnedArticleId).ShouldBe(PinnedStockBalanceId);
    }

    [Fact]
    public async Task Snapshot_StockBalance_PassesThroughWmsCoreBodyUnchanged()
    {
        var otherHu = Guid.CreateVersion7();
        var otherArticle = Guid.CreateVersion7();
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "StockBalance",
            page = 1,
            seq = 17L,
            snapshot_schema = SnapshotSchemaM1a,
            items = new object[]
            {
                new
                {
                    id = PinnedStockBalanceId,
                    location_id = Guid.CreateVersion7(),
                    article_id = PinnedArticleId,
                    handling_unit_id = PinnedHuId,
                    qty_base = "12.5",
                    reserved_qty_base = "1"
                },
                new
                {
                    id = StockBalanceChangeId(otherHu, otherArticle),
                    location_id = Guid.CreateVersion7(),
                    article_id = otherArticle,
                    handling_unit_id = otherHu,
                    qty_base = "1",
                    reserved_qty_base = "0"
                }
            }
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, _) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync(
            $"/sync/snapshot?warehouse={_warehouseId}&entity=StockBalance&page=1");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertJsonEqual(expectedBody, actualBody);
        actualBody.GetProperty("seq").GetInt64().ShouldBe(17);
        actualBody.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(PinnedStockBalanceId);
    }

    [Fact]
    public async Task Snapshot_Deviation_PassesThroughWmsCoreBodyUnchanged()
    {
        var commandId = Guid.CreateVersion7();
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "Deviation",
            page = 1,
            seq = 21L,
            snapshot_schema = SnapshotSchemaM1a,
            items = new object[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    warehouse_id = _warehouseId,
                    kind = "occupied_bin",
                    status = "open",
                    command_id = commandId,
                    detail = OccupiedBinDetail(
                        binLocationId: Guid.CreateVersion7(),
                        incomingHuId: Guid.CreateVersion7(),
                        existingHuId: Guid.CreateVersion7(),
                        lpn: "LPN-INCOMING",
                        commandId: commandId),
                    created_at = DateTimeOffset.UtcNow
                },
                new
                {
                    id = Guid.CreateVersion7(),
                    warehouse_id = _warehouseId,
                    kind = "stale_command",
                    status = "pending_approval",
                    command_id = (Guid?)null,
                    detail = JsonSerializer.SerializeToElement(new { }),
                    created_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, _) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync(
            $"/sync/snapshot?warehouse={_warehouseId}&entity=Deviation&page=1");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertJsonEqual(expectedBody, actualBody);

        var items = actualBody.GetProperty("items").EnumerateArray().ToArray();
        items.Length.ShouldBe(2);
        items[0].GetProperty("status").GetString().ShouldBe("open");
        items[1].GetProperty("status").GetString().ShouldBe("pending_approval");
        actualBody.GetProperty("seq").GetInt64().ShouldBe(21);
    }

    [Fact]
    public async Task Snapshot_SeqFeedEpochWarehousePage_PassesThroughUnchanged()
    {
        JsonElement PageBody(int page, long seq) => JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "StockBalance",
            page,
            seq,
            snapshot_schema = SnapshotSchemaM1a,
            items = Array.Empty<object>()
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, page) =>
            page == 2 ? PageBody(2, 99) : PageBody(1, 42);

        using var client = Authed();
        var page1 = await client.GetAsync(
            $"/sync/snapshot?warehouse={_warehouseId}&entity=StockBalance&page=1");
        page1.EnsureSuccessStatusCode();
        var body1 = await page1.Content.ReadFromJsonAsync<JsonElement>();
        body1.GetProperty("seq").GetInt64().ShouldBe(42);
        body1.GetProperty("feed_epoch").GetGuid().ShouldBe(_factory.Wms.FeedEpoch);
        body1.GetProperty("warehouse").GetGuid().ShouldBe(_warehouseId);
        body1.GetProperty("page").GetInt32().ShouldBe(1);

        var page2 = await client.GetAsync(
            $"/sync/snapshot?warehouse={_warehouseId}&entity=StockBalance&page=2");
        page2.EnsureSuccessStatusCode();
        var body2 = await page2.Content.ReadFromJsonAsync<JsonElement>();
        body2.GetProperty("seq").GetInt64().ShouldBe(99);
        body2.GetProperty("feed_epoch").GetGuid().ShouldBe(_factory.Wms.FeedEpoch);
        body2.GetProperty("warehouse").GetGuid().ShouldBe(_warehouseId);
        body2.GetProperty("page").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Snapshot_MissingSnapshotSchema_PassesThroughAsIs()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "Task",
            page = 1,
            seq = 0L,
            items = Array.Empty<object>()
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, _) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=Task");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        actualBody.TryGetProperty("snapshot_schema", out _).ShouldBeFalse();
        actualBody.GetProperty("seq").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task Snapshot_DifferentSnapshotSchema_PassesThroughUnchanged()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "Article",
            page = 1,
            seq = 3L,
            snapshot_schema = "CustomSchemaValue_2024",
            items = Array.Empty<object>()
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, _) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=Article");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        actualBody.GetProperty("snapshot_schema").GetString().ShouldBe("CustomSchemaValue_2024");
        actualBody.GetProperty("seq").GetInt64().ShouldBe(3);
    }

    [Fact]
    public async Task Snapshot_QtyBaseDecimalString_Unchanged()
    {
        var huId = Guid.CreateVersion7();
        var articleId = Guid.CreateVersion7();
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "StockBalance",
            page = 1,
            seq = 8L,
            snapshot_schema = SnapshotSchemaM1a,
            items = new[]
            {
                new
                {
                    id = StockBalanceChangeId(huId, articleId),
                    location_id = Guid.CreateVersion7(),
                    article_id = articleId,
                    handling_unit_id = huId,
                    qty_base = "12.5",
                    reserved_qty_base = "0"
                }
            }
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, _) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync(
            $"/sync/snapshot?warehouse={_warehouseId}&entity=StockBalance");
        response.EnsureSuccessStatusCode();

        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        item.GetProperty("id").GetGuid().ShouldBe(StockBalanceChangeId(huId, articleId));
        AssertDecimalString(item.GetProperty("qty_base"), "12.5");
        AssertDecimalString(item.GetProperty("reserved_qty_base"), "0");
    }

    [Fact]
    public async Task Changes_QtyBaseDecimalString_Unchanged()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entries = new[]
            {
                StockBalanceRow(200, Guid.CreateVersion7(), PinnedHuId, Guid.CreateVersion7(), PinnedArticleId, "12.5", DateTimeOffset.UtcNow, reservedQtyBase: "1")
            }
        });

        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=199");
        response.EnsureSuccessStatusCode();

        var entry = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entries")[0];
        entry.GetProperty("id").GetGuid().ShouldBe(PinnedStockBalanceId);
        var payload = entry.GetProperty("payload");
        payload.GetProperty("id").GetGuid().ShouldBe(PinnedStockBalanceId);
        payload.GetProperty("handling_unit_id").GetGuid().ShouldBe(PinnedHuId);
        payload.GetProperty("article_id").GetGuid().ShouldBe(PinnedArticleId);
        AssertDecimalString(payload.GetProperty("qty_base"), "12.5");
        AssertDecimalString(payload.GetProperty("reserved_qty_base"), "1");
    }

    [Fact]
    public async Task Changes_WmsCore410_PassesStatusAndBodyThrough()
    {
        _factory.Wms.ChangesStatus = HttpStatusCode.Gone;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=0");

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await response.Content.ReadAsStringAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task OccupiedBinChanges_PassesThroughWmsCoreBodyUnchanged()
    {
        var expectedBody = OccupiedBinChangesBody();
        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=99");
        response.EnsureSuccessStatusCode();

        AssertJsonEqual(expectedBody, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Changes_RowWarehouseId_UnchangedIncludingTenantWideNull()
    {
        var expectedBody = OccupiedBinChangesBody();
        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=99");
        response.EnsureSuccessStatusCode();

        var expectedEntries = expectedBody.GetProperty("entries").EnumerateArray().ToArray();
        var actualEntries = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("entries").EnumerateArray().ToArray();

        actualEntries.Length.ShouldBe(6);
        actualEntries.Length.ShouldBe(expectedEntries.Length);

        for (var i = 0; i < expectedEntries.Length; i++)
        {
            expectedEntries[i].TryGetProperty("warehouse_id", out var expectedWarehouse).ShouldBeTrue();
            actualEntries[i].TryGetProperty("warehouse_id", out var actualWarehouse).ShouldBeTrue(
                $"warehouse_id missing on public change row {i} ({actualEntries[i].GetProperty("entity").GetString()} {actualEntries[i].GetProperty("op").GetString()})");
            actualWarehouse.ValueKind.ShouldBe(expectedWarehouse.ValueKind);
            if (expectedWarehouse.ValueKind == JsonValueKind.Null)
            {
                actualWarehouse.ValueKind.ShouldBe(JsonValueKind.Null);
            }
            else
            {
                actualWarehouse.GetGuid().ShouldBe(expectedWarehouse.GetGuid());
            }
        }

        var article = actualEntries.Single(e => e.GetProperty("entity").GetString() == "article");
        article.GetProperty("warehouse_id").ValueKind.ShouldBe(JsonValueKind.Null);

        var occupied = actualEntries.Where(e => e.GetProperty("entity").GetString() != "article").ToArray();
        occupied.Length.ShouldBe(5);
        foreach (var row in occupied)
        {
            row.GetProperty("op").GetString().ShouldBe("upsert");
            row.TryGetProperty("warehouse_id", out var warehouseId).ShouldBeTrue();
            warehouseId.ValueKind.ShouldBe(JsonValueKind.String);
            warehouseId.GetGuid().ShouldBe(_warehouseId);
        }
    }

    [Fact]
    public async Task OccupiedBinChanges_FiveRowsKeepUnlocatedBalanceAndWarehouseId()
    {
        var expectedBody = OccupiedBinChangesBody();
        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=99");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertJsonEqual(expectedBody, actualBody);

        var expectedEntries = expectedBody.GetProperty("entries").EnumerateArray().ToArray();
        var actualEntries = actualBody.GetProperty("entries").EnumerateArray().ToArray();
        var occupiedExpected = expectedEntries
            .Where(e => e.GetProperty("entity").GetString() != "article")
            .ToArray();
        var occupiedActual = actualEntries
            .Where(e => e.GetProperty("entity").GetString() != "article")
            .ToArray();
        occupiedActual.Length.ShouldBe(5);
        occupiedExpected.Length.ShouldBe(5);

        var commandId = occupiedExpected[0].GetProperty("command_id").GetGuid();
        foreach (var row in occupiedActual)
        {
            row.GetProperty("command_id").GetGuid().ShouldBe(commandId);
            row.GetProperty("op").GetString().ShouldBe("upsert");
            row.TryGetProperty("warehouse_id", out var warehouseId).ShouldBeTrue();
            warehouseId.ValueKind.ShouldBe(JsonValueKind.String);
            warehouseId.GetGuid().ShouldBe(_warehouseId);
        }

        AssertOccupiedBinRows(occupiedExpected, occupiedActual);
    }

    [Fact]
    public async Task Snapshot_WarehouseQuery_PassesThroughToWmsCore()
    {
        Guid? capturedWarehouse = null;
        _factory.Wms.OnGetSnapshot = (_, wh, _, _) =>
        {
            capturedWarehouse = wh;
            return JsonSerializer.SerializeToElement(new
            {
                feed_epoch = _factory.Wms.FeedEpoch,
                warehouse = wh,
                entity = "StockBalance",
                page = 1,
                seq = 0L,
                snapshot_schema = SnapshotSchemaM1a,
                items = Array.Empty<object>()
            });
        };

        using var client = Authed();
        await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=StockBalance");

        capturedWarehouse.ShouldBe(_warehouseId);
    }

    [Fact]
    public async Task Changes_WarehouseQuery_PassesThroughToWmsCore()
    {
        Guid? capturedWarehouse = null;

        _factory.Wms.OnGetChangesWithParams = (_, wh, _) =>
        {
            capturedWarehouse = wh;
            return JsonSerializer.SerializeToElement(new
            {
                feed_epoch = _factory.Wms.FeedEpoch,
                warehouse = wh,
                entries = Array.Empty<object>()
            });
        };

        using var client = Authed();
        var warehouseToQuery = Guid.CreateVersion7();
        var response = await client.GetAsync($"/sync/changes?warehouse={warehouseToQuery}&since=0");
        response.EnsureSuccessStatusCode();

        capturedWarehouse.ShouldBe(warehouseToQuery);

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        actualBody.GetProperty("warehouse").GetGuid().ShouldBe(warehouseToQuery);
    }

    [Fact]
    public async Task Changes_OmittedField_PassesThroughAsIs()
    {
        var payloadWithoutOptional = JsonDocument.Parse("""
            {
                "id": "01234567-89ab-cdef-0123-456789abcdef",
                "warehouse_id": "01234567-89ab-cdef-0123-456789abcdef",
                "type": "putaway",
                "status": "done"
            }
            """).RootElement;

        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entries = new[]
            {
                new
                {
                    seq = 300L,
                    entity = "task",
                    id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
                    op = "upsert",
                    payload = payloadWithoutOptional,
                    command_id = (Guid?)null,
                    actor = (Guid?)null,
                    warehouse_id = _warehouseId,
                    occurred_at = DateTimeOffset.UtcNow,
                    recorded_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=299");
        response.EnsureSuccessStatusCode();

        var payload = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("entries")[0]
            .GetProperty("payload");
        payload.TryGetProperty("article_id", out _).ShouldBeFalse();
        payload.TryGetProperty("suggested_location_id", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task MixedPalletChanges_TwoStockBalancesDistinctIds()
    {
        var commandId = Guid.CreateVersion7();
        var huId = Guid.CreateVersion7();
        var articleA = Guid.CreateVersion7();
        var articleB = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var idA = StockBalanceChangeId(huId, articleA);
        var idB = StockBalanceChangeId(huId, articleB);
        idA.ShouldNotBe(idB);
        StockBalanceChangeId(huId, articleA).ShouldBe(idA);

        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entries = new object[]
            {
                StockBalanceRow(400, commandId, huId, locationId, articleA, "12.5", now),
                StockBalanceRow(401, commandId, huId, locationId, articleB, "1", now)
            }
        });

        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=399");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertJsonEqual(expectedBody, actualBody);

        var entries = actualBody.GetProperty("entries").EnumerateArray().ToArray();
        entries.Length.ShouldBe(2);
        entries[0].GetProperty("id").GetGuid().ShouldBe(idA);
        entries[1].GetProperty("id").GetGuid().ShouldBe(idB);
        entries[0].GetProperty("id").GetGuid().ShouldNotBe(entries[1].GetProperty("id").GetGuid());

        foreach (var entry in entries)
        {
            var payload = entry.GetProperty("payload");
            payload.GetProperty("id").GetGuid().ShouldBe(entry.GetProperty("id").GetGuid());
            payload.GetProperty("handling_unit_id").GetGuid().ShouldBe(huId);
            payload.TryGetProperty("article_id", out var article).ShouldBeTrue();
            article.ValueKind.ShouldBe(JsonValueKind.String);
            entry.GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);
        }

        entries[0].GetProperty("payload").GetProperty("article_id").GetGuid().ShouldBe(articleA);
        entries[1].GetProperty("payload").GetProperty("article_id").GetGuid().ShouldBe(articleB);
        AssertDecimalString(entries[0].GetProperty("payload").GetProperty("qty_base"), "12.5");
        AssertDecimalString(entries[1].GetProperty("payload").GetProperty("qty_base"), "1");
    }

    [Fact]
    public async Task Snapshot_NullArticleIdIncluded_PassesThroughAsIs()
    {
        var huId = Guid.CreateVersion7();
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "StockBalance",
            page = 1,
            seq = 4L,
            snapshot_schema = SnapshotSchemaM1a,
            items = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    location_id = Guid.CreateVersion7(),
                    article_id = (Guid?)null,
                    handling_unit_id = huId,
                    qty_base = "1",
                    reserved_qty_base = "0"
                }
            }
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, _) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync(
            $"/sync/snapshot?warehouse={_warehouseId}&entity=StockBalance");
        response.EnsureSuccessStatusCode();

        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        item.TryGetProperty("article_id", out var articleIdProp).ShouldBeTrue();
        articleIdProp.ValueKind.ShouldBe(JsonValueKind.Null);
        AssertDecimalString(item.GetProperty("qty_base"), "1");
    }

    // Occupied-bin change page matches WmsCore PR 8 @ 500117c. Incoming HU
    // putaway into the bin; existing HU stock_balance moves to the warehouse
    // UNLOCATED system location (is_system, code UNLOCATED) and keeps qty.
    // handling_unit payloads have no location. Existing HU gets no HU row.
    // Deviation detail is location_id, existing_handling_unit_id,
    // incoming_handling_unit_id, lpn, command_id.
    private JsonElement OccupiedBinChangesBody()
    {
        var commandId = Guid.CreateVersion7();
        var incomingHuId = Guid.CreateVersion7();
        var existingHuId = Guid.CreateVersion7();
        var incomingArticleId = Guid.CreateVersion7();
        var existingArticleId = Guid.CreateVersion7();
        var binLocationId = Guid.CreateVersion7();
        var suggestedLocationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var deviationId = Guid.CreateVersion7();
        const string existingQtyBase = "1";
        const string incomingQtyBase = "12.5";
        const string incomingLpn = "LPN-INCOMING";
        var now = DateTimeOffset.UtcNow;

        return JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entries = new object[]
            {
                HandlingUnitRow(100, commandId, incomingHuId, incomingLpn, incomingArticleId, incomingQtyBase, now),
                StockBalanceRow(101, commandId, incomingHuId, binLocationId, incomingArticleId, incomingQtyBase, now),
                StockBalanceRow(102, commandId, existingHuId, _unlocatedLocationId, existingArticleId, existingQtyBase, now),
                new
                {
                    seq = 103L,
                    entity = "deviation",
                    id = deviationId,
                    op = DeviationChangeOp,
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        id = deviationId,
                        warehouse_id = _warehouseId,
                        kind = "occupied_bin",
                        status = "open",
                        command_id = commandId,
                        detail = OccupiedBinDetail(
                            binLocationId,
                            incomingHuId,
                            existingHuId,
                            incomingLpn,
                            commandId),
                        created_at = now
                    }),
                    command_id = commandId,
                    actor = _userId,
                    warehouse_id = _warehouseId,
                    occurred_at = now,
                    recorded_at = now
                },
                new
                {
                    seq = 104L,
                    entity = "task",
                    id = taskId,
                    op = "upsert",
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        id = taskId,
                        warehouse_id = _warehouseId,
                        type = "putaway",
                        status = "done",
                        assignee_user_id = _userId,
                        assigned_until = (DateTimeOffset?)null,
                        suggested_location_id = suggestedLocationId,
                        created_at = now
                    }),
                    command_id = commandId,
                    actor = _userId,
                    warehouse_id = _warehouseId,
                    occurred_at = now,
                    recorded_at = now
                },
                new
                {
                    seq = 105L,
                    entity = "article",
                    id = incomingArticleId,
                    op = "upsert",
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        id = incomingArticleId,
                        sku = "SKU-1",
                        name = "Widget",
                        status = "published",
                        base_uom_id = Guid.Parse("01900000-0000-7000-8000-000000000101"),
                        quantity_precision = 0,
                        quantity_step = "1",
                        allow_loose_pick = true,
                        packaging_levels = Array.Empty<object>()
                    }),
                    command_id = Guid.CreateVersion7(),
                    actor = _userId,
                    warehouse_id = (Guid?)null,
                    occurred_at = now,
                    recorded_at = now
                }
            }
        });
    }

    // Matches RC HandlingUnitDto + InventoryJson (no location_id; null height_mm omitted).
    private object HandlingUnitRow(
        long seq,
        Guid commandId,
        Guid huId,
        string lpn,
        Guid articleId,
        string qtyBase,
        DateTimeOffset now) =>
        new
        {
            seq,
            entity = "handling_unit",
            id = huId,
            op = "upsert",
            payload = JsonSerializer.SerializeToElement(new
            {
                id = huId,
                warehouse_id = _warehouseId,
                lpn,
                received_at = now,
                contents = new[]
                {
                    new
                    {
                        id = Guid.CreateVersion7(),
                        handling_unit_id = huId,
                        article_id = articleId,
                        qty_base = qtyBase,
                        packaging_level_id = Guid.CreateVersion7()
                    }
                }
            }),
            command_id = commandId,
            actor = _userId,
            warehouse_id = _warehouseId,
            occurred_at = now,
            recorded_at = now
        };

    private object StockBalanceRow(
        long seq,
        Guid commandId,
        Guid huId,
        Guid locationId,
        Guid articleId,
        string qtyBase,
        DateTimeOffset now,
        string reservedQtyBase = "0")
    {
        var id = StockBalanceChangeId(huId, articleId);
        return new
        {
            seq,
            entity = "stock_balance",
            id,
            op = "upsert",
            payload = JsonSerializer.SerializeToElement(new
            {
                id,
                location_id = locationId,
                article_id = articleId,
                handling_unit_id = huId,
                qty_base = qtyBase,
                reserved_qty_base = reservedQtyBase
            }),
            command_id = commandId,
            actor = _userId,
            warehouse_id = _warehouseId,
            occurred_at = now,
            recorded_at = now
        };
    }

    private static Guid StockBalanceChangeId(Guid handlingUnitId, Guid articleId)
    {
        var name = $"hu:{handlingUnitId:D}:{articleId:D}".ToLowerInvariant();
        return UuidV5(StockBalanceChangeIdNamespace, Encoding.UTF8.GetBytes(name));
    }

    // RFC 4122 name-based UUIDv5. .NET 10 on this SDK has no Guid.CreateVersion5.
    private static Guid UuidV5(Guid namespaceId, byte[] name)
    {
        var ns = namespaceId.ToByteArray();
        SwapGuidEndianness(ns);
        var hash = SHA1.HashData(ns.Concat(name).ToArray());
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        SwapGuidEndianness(bytes);
        return new Guid(bytes);
    }

    private static void SwapGuidEndianness(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }

    // WmsCore PR 8 occupied_bin detail (ConfirmPutawayHandler). No movement ids.
    private static JsonElement OccupiedBinDetail(
        Guid binLocationId,
        Guid incomingHuId,
        Guid existingHuId,
        string lpn,
        Guid commandId) =>
        JsonSerializer.SerializeToElement(new
        {
            location_id = binLocationId,
            existing_handling_unit_id = existingHuId,
            incoming_handling_unit_id = incomingHuId,
            lpn,
            command_id = commandId
        });

    private void AssertOccupiedBinRows(JsonElement[] expected, JsonElement[] actual)
    {
        actual.Length.ShouldBe(5);
        for (var i = 0; i < expected.Length; i++)
        {
            AssertJsonEqual(expected[i], actual[i]);
        }

        actual.Count(e => e.GetProperty("entity").GetString() == "handling_unit").ShouldBe(1);
        var incomingHu = actual.Single(e => e.GetProperty("entity").GetString() == "handling_unit");
        incomingHu.GetProperty("payload").TryGetProperty("location_id", out _).ShouldBeFalse();
        var incomingHuId = incomingHu.GetProperty("id").GetGuid();
        var commandId = incomingHu.GetProperty("command_id").GetGuid();
        var incomingLpn = incomingHu.GetProperty("payload").GetProperty("lpn").GetString();

        var balances = actual.Where(e => e.GetProperty("entity").GetString() == "stock_balance").ToArray();
        balances.Length.ShouldBe(2);
        var incomingBal = balances.Single(e =>
            e.GetProperty("payload").GetProperty("handling_unit_id").GetGuid() == incomingHuId);
        var existingBal = balances.Single(e =>
            e.GetProperty("payload").GetProperty("handling_unit_id").GetGuid() != incomingHuId);
        var existingHuId = existingBal.GetProperty("payload").GetProperty("handling_unit_id").GetGuid();
        var incomingArticleId = incomingBal.GetProperty("payload").GetProperty("article_id").GetGuid();
        var existingArticleId = existingBal.GetProperty("payload").GetProperty("article_id").GetGuid();
        incomingBal.GetProperty("id").GetGuid().ShouldBe(StockBalanceChangeId(incomingHuId, incomingArticleId));
        existingBal.GetProperty("id").GetGuid().ShouldBe(StockBalanceChangeId(existingHuId, existingArticleId));
        incomingBal.GetProperty("id").GetGuid().ShouldNotBe(existingBal.GetProperty("id").GetGuid());
        incomingBal.GetProperty("payload").GetProperty("id").GetGuid().ShouldBe(incomingBal.GetProperty("id").GetGuid());
        existingBal.GetProperty("payload").GetProperty("id").GetGuid().ShouldBe(existingBal.GetProperty("id").GetGuid());

        incomingBal.GetProperty("payload").GetProperty("location_id").ValueKind.ShouldBe(JsonValueKind.String);
        incomingBal.GetProperty("payload").GetProperty("location_id").GetGuid().ShouldNotBe(_unlocatedLocationId);
        existingBal.GetProperty("payload").GetProperty("location_id").ValueKind.ShouldBe(JsonValueKind.String);
        existingBal.GetProperty("payload").GetProperty("location_id").GetGuid().ShouldBe(_unlocatedLocationId);
        incomingBal.GetProperty("payload").GetProperty("handling_unit_id").GetGuid().ShouldBe(incomingHuId);
        existingBal.GetProperty("payload").GetProperty("handling_unit_id").GetGuid().ShouldBe(existingHuId);
        incomingBal.GetProperty("payload").GetProperty("article_id").GetGuid().ShouldBe(incomingArticleId);
        existingBal.GetProperty("payload").GetProperty("article_id").GetGuid().ShouldBe(existingArticleId);
        AssertDecimalString(existingBal.GetProperty("payload").GetProperty("qty_base"), "1");
        AssertDecimalString(incomingBal.GetProperty("payload").GetProperty("qty_base"), "12.5");

        foreach (var row in actual)
        {
            row.TryGetProperty("warehouse_id", out var warehouseId).ShouldBeTrue();
            warehouseId.ValueKind.ShouldBe(JsonValueKind.String);
            warehouseId.GetGuid().ShouldBe(_warehouseId);
        }

        existingBal.GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);

        var deviation = actual.Single(e => e.GetProperty("entity").GetString() == "deviation");
        deviation.GetProperty("op").GetString().ShouldBe(DeviationChangeOp);
        deviation.GetProperty("payload").GetProperty("status").GetString().ShouldBe("open");
        var detail = deviation.GetProperty("payload").GetProperty("detail");
        detail.GetProperty("incoming_handling_unit_id").GetGuid().ShouldBe(incomingHuId);
        detail.GetProperty("existing_handling_unit_id").GetGuid().ShouldBe(existingHuId);
        detail.GetProperty("lpn").GetString().ShouldBe(incomingLpn);
        detail.GetProperty("command_id").GetGuid().ShouldBe(commandId);
        detail.TryGetProperty("movement_id", out _).ShouldBeFalse();
        detail.TryGetProperty("displaced_movement_id", out _).ShouldBeFalse();

        actual.Count(e => e.GetProperty("entity").GetString() == "task").ShouldBe(1);
    }

    private static void AssertJsonEqual(JsonElement expected, JsonElement actual)
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        JsonSerializer.Serialize(actual, options).ShouldBe(JsonSerializer.Serialize(expected, options));
    }

    private static void AssertDecimalString(JsonElement value, string digits)
    {
        value.ValueKind.ShouldBe(JsonValueKind.String);
        value.GetString().ShouldBe(digits);
    }

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        var token = _factory.IssueToken(_userId, _tenantId, 1, _deviceId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
