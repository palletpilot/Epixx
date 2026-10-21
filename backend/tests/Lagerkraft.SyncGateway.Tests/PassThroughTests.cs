using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.SyncGateway.Clients;

namespace Lagerkraft.SyncGateway.Tests;

[Trait("Category", "Integration")]
public sealed class PassThroughTests : IAsyncLifetime
{
    // RC change_log writes one op. Occupied-bin deviation uses the same vocabulary.
    private const string DeviationChangeOp = "upsert";

    private readonly SyncGatewayApiFactory _factory = new();
    private Guid _tenantId;
    private Guid _userId;
    private Guid _deviceId;
    private Guid _warehouseId;

    public Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _userId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _warehouseId = Guid.CreateVersion7();
        _factory.Platform.Devices[_deviceId] = new InternalDevice(
            _deviceId, _tenantId, "floor-1", [_tenantId], null, 0);
        _factory.Platform.Entitlement = new TenantEntitlement(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.CurrentSessionVersion = 1;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Snapshot_StockBalance_PassesThroughWmsCoreBodyUnchanged()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "StockBalance",
            page = 1,
            snapshot_schema = "20260919164855_OutboundTables",
            items = new object[]
            {
                new
                {
                    location_id = Guid.CreateVersion7(),
                    article_id = Guid.CreateVersion7(),
                    handling_unit_id = Guid.CreateVersion7(),
                    qty_base = "12.5",
                    reserved_qty_base = "1"
                },
                new
                {
                    location_id = Guid.CreateVersion7(),
                    article_id = Guid.CreateVersion7(),
                    handling_unit_id = Guid.CreateVersion7(),
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

        AssertJsonEqual(expectedBody, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task Snapshot_Deviation_PassesThroughWmsCoreBodyUnchanged()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "Deviation",
            page = 1,
            snapshot_schema = "20260919164855_OutboundTables",
            items = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    warehouse_id = _warehouseId,
                    kind = "occupied_bin",
                    command_id = Guid.CreateVersion7(),
                    detail = OccupiedBinDetail(
                        binLocationId: Guid.CreateVersion7(),
                        incomingHuId: Guid.CreateVersion7(),
                        existingHuId: Guid.CreateVersion7(),
                        incomingMovementId: Guid.CreateVersion7(),
                        displacedMovementId: Guid.CreateVersion7(),
                        incomingQtyBase: "12.5",
                        existingQtyBase: "1"),
                    created_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, _) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync(
            $"/sync/snapshot?warehouse={_warehouseId}&entity=Deviation&page=1");
        response.EnsureSuccessStatusCode();

        AssertJsonEqual(expectedBody, await response.Content.ReadFromJsonAsync<JsonElement>());
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
            items = Array.Empty<object>()
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, _) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=Task");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        actualBody.TryGetProperty("snapshot_schema", out _).ShouldBeFalse();
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
            snapshot_schema = "CustomSchemaValue_2024",
            items = Array.Empty<object>()
        });

        _factory.Wms.OnGetSnapshot = (_, _, _, _) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=Article");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        actualBody.GetProperty("snapshot_schema").GetString().ShouldBe("CustomSchemaValue_2024");
    }

    [Fact]
    public async Task Snapshot_QtyBaseDecimalString_Unchanged()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "StockBalance",
            page = 1,
            snapshot_schema = "20260919164855_OutboundTables",
            items = new[]
            {
                new
                {
                    location_id = Guid.CreateVersion7(),
                    article_id = Guid.CreateVersion7(),
                    handling_unit_id = Guid.CreateVersion7(),
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
                new
                {
                    seq = 200L,
                    entity = "stock_balance",
                    id = Guid.CreateVersion7(),
                    op = "upsert",
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        location_id = Guid.CreateVersion7(),
                        article_id = Guid.CreateVersion7(),
                        handling_unit_id = Guid.CreateVersion7(),
                        qty_base = "12.5",
                        reserved_qty_base = "1"
                    }),
                    command_id = Guid.CreateVersion7(),
                    actor = _userId,
                    warehouse_id = _warehouseId,
                    occurred_at = DateTimeOffset.UtcNow,
                    recorded_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=199");
        response.EnsureSuccessStatusCode();

        var payload = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("entries")[0]
            .GetProperty("payload");
        AssertDecimalString(payload.GetProperty("qty_base"), "12.5");
        AssertDecimalString(payload.GetProperty("reserved_qty_base"), "1");
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

        actualEntries.Length.ShouldBe(7);
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
        occupied.Length.ShouldBe(6);
        foreach (var row in occupied)
        {
            row.GetProperty("op").GetString().ShouldBe("upsert");
            row.GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);
        }
    }

    [Fact]
    public async Task OccupiedBinChanges_SixRowsKeepNullLocationAndIds()
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
        occupiedActual.Length.ShouldBe(6);
        occupiedExpected.Length.ShouldBe(6);

        var commandId = occupiedExpected[0].GetProperty("command_id").GetGuid();
        foreach (var row in occupiedActual)
        {
            row.GetProperty("command_id").GetGuid().ShouldBe(commandId);
            row.GetProperty("op").GetString().ShouldBe("upsert");
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
                snapshot_schema = "20260919164855_OutboundTables",
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
    public async Task Changes_NullArticleIdOmitted_PassesThroughAsIs()
    {
        var payloadWithoutArticleId = JsonDocument.Parse("""
            {
                "location_id": "01234567-89ab-cdef-0123-456789abcdef",
                "handling_unit_id": "01234567-89ab-cdef-0123-456789abcdef",
                "qty_base": "1",
                "reserved_qty_base": "0"
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
                    entity = "stock_balance",
                    id = Guid.CreateVersion7(),
                    op = "upsert",
                    payload = payloadWithoutArticleId,
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
        AssertDecimalString(payload.GetProperty("qty_base"), "1");
    }

    [Fact]
    public async Task Snapshot_NullArticleIdIncluded_PassesThroughAsIs()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "StockBalance",
            page = 1,
            snapshot_schema = "20260919164855_OutboundTables",
            items = new[]
            {
                new
                {
                    location_id = Guid.CreateVersion7(),
                    article_id = (Guid?)null,
                    handling_unit_id = Guid.CreateVersion7(),
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

    // Occupied-bin change page is provisional: wms-core is still writing this wire
    // (spec PR 5). Incoming HU putaway into the bin; existing HU moves out to no
    // location and keeps qty. Existing outbound movement is displaced_movement_id.
    private JsonElement OccupiedBinChangesBody()
    {
        var commandId = Guid.CreateVersion7();
        var incomingHuId = Guid.CreateVersion7();
        var existingHuId = Guid.CreateVersion7();
        var incomingArticleId = Guid.CreateVersion7();
        var existingArticleId = Guid.CreateVersion7();
        var binLocationId = Guid.CreateVersion7();
        var suggestedLocationId = Guid.CreateVersion7();
        var incomingMovementId = Guid.CreateVersion7();
        var displacedMovementId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var deviationId = Guid.CreateVersion7();
        const string existingQtyBase = "1";
        const string incomingQtyBase = "12.5";
        var now = DateTimeOffset.UtcNow;

        return JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entries = new object[]
            {
                HandlingUnitRow(100, commandId, incomingHuId, "LPN-INCOMING", binLocationId, incomingArticleId, incomingQtyBase, now),
                HandlingUnitRow(101, commandId, existingHuId, "LPN-EXISTING", locationId: null, existingArticleId, existingQtyBase, now),
                StockBalanceRow(102, commandId, incomingHuId, binLocationId, incomingArticleId, incomingQtyBase, now),
                StockBalanceRow(103, commandId, existingHuId, locationId: null, existingArticleId, existingQtyBase, now),
                new
                {
                    seq = 104L,
                    entity = "deviation",
                    id = deviationId,
                    op = DeviationChangeOp,
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        id = deviationId,
                        warehouse_id = _warehouseId,
                        kind = "occupied_bin",
                        command_id = commandId,
                        detail = OccupiedBinDetail(
                            binLocationId,
                            incomingHuId,
                            existingHuId,
                            incomingMovementId,
                            displacedMovementId,
                            incomingQtyBase,
                            existingQtyBase),
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
                    seq = 106L,
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

    private object HandlingUnitRow(
        long seq,
        Guid commandId,
        Guid huId,
        string lpn,
        Guid? locationId,
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
                location_id = locationId,
                lpn,
                height_mm = (int?)null,
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
        Guid? locationId,
        Guid articleId,
        string qtyBase,
        DateTimeOffset now) =>
        new
        {
            seq,
            entity = "stock_balance",
            id = huId,
            op = "upsert",
            payload = JsonSerializer.SerializeToElement(new
            {
                location_id = locationId,
                article_id = articleId,
                handling_unit_id = huId,
                qty_base = qtyBase,
                reserved_qty_base = "0"
            }),
            command_id = commandId,
            actor = _userId,
            warehouse_id = _warehouseId,
            occurred_at = now,
            recorded_at = now
        };

    // Provisional occupied_bin detail (wms-core / spec PR 5). Incoming putaway
    // movement_id; existing pallet leaves via displaced_movement_id.
    private JsonElement OccupiedBinDetail(
        Guid binLocationId,
        Guid incomingHuId,
        Guid existingHuId,
        Guid incomingMovementId,
        Guid displacedMovementId,
        string incomingQtyBase,
        string existingQtyBase) =>
        JsonSerializer.SerializeToElement(new
        {
            location_id = binLocationId,
            handling_unit_id = incomingHuId,
            existing_handling_unit_id = existingHuId,
            movement_id = incomingMovementId,
            displaced_movement_id = displacedMovementId,
            warehouse_id = _warehouseId,
            qty_base = incomingQtyBase,
            existing_qty_base = existingQtyBase
        });

    private static void AssertOccupiedBinRows(JsonElement[] expected, JsonElement[] actual)
    {
        actual.Length.ShouldBe(6);
        for (var i = 0; i < expected.Length; i++)
        {
            AssertJsonEqual(expected[i], actual[i]);
        }

        var incomingHu = actual.Single(e =>
            e.GetProperty("entity").GetString() == "handling_unit"
            && e.GetProperty("payload").GetProperty("lpn").GetString() == "LPN-INCOMING");
        var existingHu = actual.Single(e =>
            e.GetProperty("entity").GetString() == "handling_unit"
            && e.GetProperty("payload").GetProperty("lpn").GetString() == "LPN-EXISTING");
        var incomingHuId = incomingHu.GetProperty("id").GetGuid();
        var existingHuId = existingHu.GetProperty("id").GetGuid();
        var binId = incomingHu.GetProperty("payload").GetProperty("location_id").GetGuid();

        existingHu.GetProperty("payload").GetProperty("location_id").ValueKind.ShouldBe(JsonValueKind.Null);

        var incomingBal = actual.Single(e =>
            e.GetProperty("entity").GetString() == "stock_balance"
            && e.GetProperty("id").GetGuid() == incomingHuId);
        var existingBal = actual.Single(e =>
            e.GetProperty("entity").GetString() == "stock_balance"
            && e.GetProperty("id").GetGuid() == existingHuId);

        incomingBal.GetProperty("payload").GetProperty("location_id").GetGuid().ShouldBe(binId);
        existingBal.GetProperty("id").GetGuid().ShouldBe(existingHuId);
        existingBal.GetProperty("payload").GetProperty("location_id").ValueKind.ShouldBe(JsonValueKind.Null);
        existingBal.GetProperty("payload").GetProperty("handling_unit_id").GetGuid().ShouldBe(existingHuId);
        AssertDecimalString(existingBal.GetProperty("payload").GetProperty("qty_base"), "1");
        AssertDecimalString(incomingBal.GetProperty("payload").GetProperty("qty_base"), "12.5");

        var deviation = actual.Single(e => e.GetProperty("entity").GetString() == "deviation");
        deviation.GetProperty("op").GetString().ShouldBe(DeviationChangeOp);
        var detail = deviation.GetProperty("payload").GetProperty("detail");
        detail.GetProperty("handling_unit_id").GetGuid().ShouldBe(incomingHuId);
        detail.GetProperty("existing_handling_unit_id").GetGuid().ShouldBe(existingHuId);
        detail.TryGetProperty("movement_id", out _).ShouldBeTrue();
        detail.TryGetProperty("displaced_movement_id", out _).ShouldBeTrue();
        detail.GetProperty("warehouse_id").GetGuid().ShouldBe(
            incomingHu.GetProperty("warehouse_id").GetGuid());

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
