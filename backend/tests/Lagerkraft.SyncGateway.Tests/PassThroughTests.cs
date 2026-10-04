using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.SyncGateway.Clients;

namespace Lagerkraft.SyncGateway.Tests;

[Trait("Category", "Integration")]
public sealed class PassThroughTests : IAsyncLifetime
{
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
    public async Task Snapshot_Stock_PassesThroughWmsCoreBodyUnchanged()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "Stock",
            page = 1,
            snapshot_schema = "20241204_AddInventory",
            items = new object[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    warehouse_id = _warehouseId,
                    location_id = Guid.CreateVersion7(),
                    handling_unit_id = (Guid?)null,
                    article_id = Guid.CreateVersion7(),
                    qty_base = 42.5m,
                    created_at = DateTimeOffset.UtcNow
                },
                new
                {
                    id = Guid.CreateVersion7(),
                    warehouse_id = _warehouseId,
                    location_id = Guid.CreateVersion7(),
                    handling_unit_id = Guid.CreateVersion7(),
                    article_id = (Guid?)null,
                    qty_base = 100,
                    created_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetSnapshot = (tid, wh, ent, pg) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=Stock&page=1");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        var actualJson = JsonSerializer.Serialize(actualBody, new JsonSerializerOptions { WriteIndented = false });
        var expectedJson = JsonSerializer.Serialize(expectedBody, new JsonSerializerOptions { WriteIndented = false });

        actualJson.ShouldBe(expectedJson);
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
            snapshot_schema = "20241010_AddDeviation",
            items = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    warehouse_id = _warehouseId,
                    kind = "count_mismatch",
                    command_id = Guid.CreateVersion7(),
                    detail = JsonSerializer.SerializeToElement(new
                    {
                        expected = 50,
                        actual = 45,
                        location_id = Guid.CreateVersion7()
                    }),
                    created_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetSnapshot = (tid, wh, ent, pg) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=Deviation&page=1");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        var actualJson = JsonSerializer.Serialize(actualBody, new JsonSerializerOptions { WriteIndented = false });
        var expectedJson = JsonSerializer.Serialize(expectedBody, new JsonSerializerOptions { WriteIndented = false });

        actualJson.ShouldBe(expectedJson);
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

        _factory.Wms.OnGetSnapshot = (tid, wh, ent, pg) => expectedBody;

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

        _factory.Wms.OnGetSnapshot = (tid, wh, ent, pg) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=Article");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        actualBody.GetProperty("snapshot_schema").GetString().ShouldBe("CustomSchemaValue_2024");
    }

    [Fact]
    public async Task Changes_StockAndDeviation_PassesThroughWmsCoreBodyUnchanged()
    {
        var commandId = Guid.CreateVersion7();
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entries = new[]
            {
                new
                {
                    seq = 100L,
                    entity = "Stock",
                    id = Guid.CreateVersion7(),
                    op = "Delete",
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        id = Guid.CreateVersion7(),
                        warehouse_id = _warehouseId,
                        location_id = Guid.CreateVersion7(),
                        article_id = Guid.CreateVersion7(),
                        qty_base = 50
                    }),
                    command_id = commandId,
                    actor = _userId,
                    occurred_at = DateTimeOffset.UtcNow,
                    recorded_at = DateTimeOffset.UtcNow
                },
                new
                {
                    seq = 101L,
                    entity = "Stock",
                    id = Guid.CreateVersion7(),
                    op = "Upsert",
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        id = Guid.CreateVersion7(),
                        warehouse_id = _warehouseId,
                        location_id = Guid.CreateVersion7(),
                        article_id = Guid.CreateVersion7(),
                        qty_base = 45.5
                    }),
                    command_id = commandId,
                    actor = _userId,
                    occurred_at = DateTimeOffset.UtcNow,
                    recorded_at = DateTimeOffset.UtcNow
                },
                new
                {
                    seq = 102L,
                    entity = "Deviation",
                    id = Guid.CreateVersion7(),
                    op = "Insert",
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        id = Guid.CreateVersion7(),
                        warehouse_id = _warehouseId,
                        kind = "adjustment",
                        detail = JsonSerializer.SerializeToElement(new { reason = "physical_count" })
                    }),
                    command_id = commandId,
                    actor = _userId,
                    occurred_at = DateTimeOffset.UtcNow,
                    recorded_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=99");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        var actualJson = JsonSerializer.Serialize(actualBody, new JsonSerializerOptions { WriteIndented = false });
        var expectedJson = JsonSerializer.Serialize(expectedBody, new JsonSerializerOptions { WriteIndented = false });

        actualJson.ShouldBe(expectedJson);
    }

    [Fact]
    public async Task Changes_QtyBaseAsJsonNumber_PassesThroughUnchanged()
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
                    entity = "Stock",
                    id = Guid.CreateVersion7(),
                    op = "Upsert",
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        id = Guid.CreateVersion7(),
                        qty_base = 123.456789m
                    }),
                    command_id = (Guid?)null,
                    actor = (Guid?)null,
                    occurred_at = DateTimeOffset.UtcNow,
                    recorded_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?since=199");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entry = actualBody.GetProperty("entries")[0];
        var qtyBase = entry.GetProperty("payload").GetProperty("qty_base").GetDecimal();

        qtyBase.ShouldBe(123.456789m);
    }

    [Fact]
    public async Task Changes_NullArticleIdOmitted_PassesThroughAsIs()
    {
        var payloadWithoutArticleId = JsonDocument.Parse("""
            {
                "id": "01234567-89ab-cdef-0123-456789abcdef",
                "warehouse_id": "01234567-89ab-cdef-0123-456789abcdef",
                "qty_base": 100
            }
            """).RootElement;

        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = (Guid?)null,
            entries = new[]
            {
                new
                {
                    seq = 300L,
                    entity = "Stock",
                    id = Guid.CreateVersion7(),
                    op = "Upsert",
                    payload = payloadWithoutArticleId,
                    command_id = (Guid?)null,
                    actor = (Guid?)null,
                    occurred_at = DateTimeOffset.UtcNow,
                    recorded_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync("/sync/changes?since=299");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entry = actualBody.GetProperty("entries")[0];
        var payload = entry.GetProperty("payload");

        payload.TryGetProperty("article_id", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Snapshot_NullArticleIdIncluded_PassesThroughAsIs()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entity = "Stock",
            page = 1,
            snapshot_schema = "20241204_AddInventory",
            items = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    article_id = (Guid?)null,
                    qty_base = 50
                }
            }
        });

        _factory.Wms.OnGetSnapshot = (tid, wh, ent, pg) => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=Stock");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = actualBody.GetProperty("items")[0];

        item.TryGetProperty("article_id", out var articleIdProp).ShouldBeTrue();
        articleIdProp.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Snapshot_WarehouseQuery_PassesThroughToWmsCore()
    {
        Guid? capturedWarehouse = null;
        _factory.Wms.OnGetSnapshot = (tid, wh, ent, pg) =>
        {
            capturedWarehouse = wh;
            return JsonSerializer.SerializeToElement(new
            {
                feed_epoch = _factory.Wms.FeedEpoch,
                warehouse = wh,
                entity = "Stock",
                page = 1,
                snapshot_schema = "test",
                items = Array.Empty<object>()
            });
        };

        using var client = Authed();
        await client.GetAsync($"/sync/snapshot?warehouse={_warehouseId}&entity=Stock");

        capturedWarehouse.ShouldBe(_warehouseId);
    }

    [Fact]
    public async Task Changes_WarehouseQuery_PassesThroughToWmsCore()
    {
        var capturedWarehouse = Guid.Empty;

        _factory.Wms.OnGetChanges = () =>
        {
            return JsonSerializer.SerializeToElement(new
            {
                feed_epoch = _factory.Wms.FeedEpoch,
                warehouse = capturedWarehouse,
                entries = Array.Empty<object>()
            });
        };

        using var testClient = _factory.CreateClient();
        var token = _factory.IssueToken(_userId, _tenantId, 1, _deviceId);
        testClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var warehouseToQuery = Guid.CreateVersion7();
        capturedWarehouse = warehouseToQuery;

        var response = await testClient.GetAsync($"/sync/changes?warehouse={warehouseToQuery}&since=0");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        actualBody.GetProperty("warehouse").GetGuid().ShouldBe(warehouseToQuery);
    }

    [Fact]
    public async Task Changes_WarehouseIdInPayload_PassesThroughUnchanged()
    {
        var expectedBody = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = _factory.Wms.FeedEpoch,
            warehouse = _warehouseId,
            entries = new[]
            {
                new
                {
                    seq = 400L,
                    entity = "Deviation",
                    id = Guid.CreateVersion7(),
                    op = "Insert",
                    payload = JsonSerializer.SerializeToElement(new
                    {
                        id = Guid.CreateVersion7(),
                        warehouse_id = _warehouseId,
                        kind = "test"
                    }),
                    command_id = (Guid?)null,
                    actor = (Guid?)null,
                    occurred_at = DateTimeOffset.UtcNow,
                    recorded_at = DateTimeOffset.UtcNow
                }
            }
        });

        _factory.Wms.OnGetChanges = () => expectedBody;

        using var client = Authed();
        var response = await client.GetAsync($"/sync/changes?warehouse={_warehouseId}&since=399");
        response.EnsureSuccessStatusCode();

        var actualBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entry = actualBody.GetProperty("entries")[0];
        var warehouseIdInPayload = entry.GetProperty("payload").GetProperty("warehouse_id").GetGuid();

        warehouseIdInPayload.ShouldBe(_warehouseId);
    }

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        var token = _factory.IssueToken(_userId, _tenantId, 1, _deviceId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
