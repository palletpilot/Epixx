using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Lagerkraft.WmsCore.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class InboundCommandTests : IAsyncLifetime
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

    public InboundCommandTests(PostgresFixture postgres) => _postgres = postgres;

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
        client.DefaultRequestHeaders.Remove("Lagerkraft-Tenant-Id");
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
    public async Task ReceiveHandlingUnit_UnknownArticle_Rejected()
    {
        var result = await Apply("ReceiveHandlingUnit", new
        {
            warehouse_id = _warehouseId,
            handling_unit_id = Guid.CreateVersion7(),
            task_id = Guid.CreateVersion7(),
            lpn = "LPN-DEADBEEF",
            article_id = Guid.CreateVersion7(),
            qty_base = "1"
        });
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("unknown_article");

        await using var db = OpenDb();
        (await db.HandlingUnits.CountAsync()).ShouldBe(0);
        (await db.Deviations.CountAsync(d => d.CommandId == result.CommandId && d.Kind == "rejected")).ShouldBe(1);
    }

    [Fact]
    public async Task ReceiveHandlingUnit_HappyPath_BalanceAtReceiving()
    {
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var result = await Apply("ReceiveHandlingUnit", ReceivePayload(huId, taskId, "LPN-KAFFE001"));
        result.Outcome.ShouldBe("Applied");

        await using var db = OpenDb();
        var receiving = await db.Locations.SingleAsync(l => l.WarehouseId == _warehouseId && l.Code == "RECEIVING");
        var hu = await db.HandlingUnits.SingleAsync(h => h.Id == huId);
        hu.Lpn.ShouldBe("LPN-KAFFE001");
        var content = await db.HandlingUnitContents.SingleAsync(c => c.HandlingUnitId == huId);
        content.QtyBase.ShouldBe(1m);
        var movement = await db.StockMovements.SingleAsync(m => m.CommandId == result.CommandId);
        movement.Reason.ShouldBe("receive");
        movement.FromLocationId.ShouldBeNull();
        movement.ToLocationId.ShouldBe(receiving.Id);
        movement.ToHandlingUnitId.ShouldBe(huId);
        var balance = await db.StockBalances.SingleAsync();
        balance.LocationId.ShouldBe(receiving.Id);
        balance.HandlingUnitId.ShouldBe(huId);
        balance.QtyBase.ShouldBe(1m);
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId);
        task.Type.ShouldBe("putaway");
        task.Status.ShouldBe("open");
        (await db.TaskLines.CountAsync(l => l.TaskId == taskId)).ShouldBe(1);
    }

    [Fact]
    public async Task ReceiveHandlingUnit_SuggestsFirstEmptyBin()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Bin(first, "A-01-01-01"))).Outcome.ShouldBe("Applied");
        (await Apply("CreateLocationBatch", Bin(second, "A-01-01-02"))).Outcome.ShouldBe("Applied");

        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var result = await Apply("ReceiveHandlingUnit", ReceivePayload(huId, taskId, "LPN-BIN001"));
        result.Outcome.ShouldBe("Applied");

        await using var db = OpenDb();
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId);
        task.SuggestedLocationId.ShouldBe(first);
        var reservation = await db.LocationReservations.SingleAsync();
        reservation.LocationId.ShouldBe(first);
        reservation.TaskId.ShouldBe(taskId);
    }

    [Fact]
    public async Task ReceiveHandlingUnit_DuplicateLpn_Rejected()
    {
        (await Apply("ReceiveHandlingUnit", ReceivePayload(Guid.CreateVersion7(), Guid.CreateVersion7(), "LPN-DUP")))
            .Outcome.ShouldBe("Applied");
        var result = await Apply("ReceiveHandlingUnit", ReceivePayload(Guid.CreateVersion7(), Guid.CreateVersion7(), "LPN-DUP"));
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("duplicate_lpn");
        await using var db = OpenDb();
        (await db.HandlingUnits.CountAsync(h => h.Lpn == "LPN-DUP")).ShouldBe(1);
    }

    [Fact]
    public async Task ReceiveHandlingUnit_Viewer_Forbidden()
    {
        var result = await Apply(
            "ReceiveHandlingUnit",
            ReceivePayload(Guid.CreateVersion7(), Guid.CreateVersion7(), "LPN-VIEW"),
            userId: _viewerId);
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("forbidden");
        await using var db = OpenDb();
        (await db.HandlingUnits.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ReceiveHandlingUnit_IdempotentRetry()
    {
        var huId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        var payload = ReceivePayload(huId, taskId, "LPN-RETRY");
        var first = await Apply("ReceiveHandlingUnit", payload, commandId: commandId);
        var second = await Apply("ReceiveHandlingUnit", payload, commandId: commandId);
        first.Outcome.ShouldBe("Applied");
        second.Outcome.ShouldBe("Applied");
        second.CommandId.ShouldBe(commandId);
        await using var db = OpenDb();
        (await db.HandlingUnits.CountAsync(h => h.Id == huId)).ShouldBe(1);
        (await db.StockMovements.CountAsync(m => m.CommandId == commandId)).ShouldBe(1);
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
        Guid? commandId = null,
        DateTimeOffset? occurredAt = null)
    {
        using var client = InternalClient();
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

    private sealed record BatchResultDto(List<CommandResultDto> Results);
    private sealed record CommandResultDto(
        Guid CommandId,
        string Outcome,
        string? Code,
        string? Message,
        JsonElement Details);
}
