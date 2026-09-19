using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LayoutCommandTests : IAsyncLifetime
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
    private string _cs = "";

    public LayoutCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _workerId = Guid.CreateVersion7();
        _viewerId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _warehouseId = Guid.CreateVersion7();
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
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task CreateLocationBatch_UnknownParent_Rejected()
    {
        var missingParent = Guid.CreateVersion7();
        var childId = Guid.CreateVersion7();
        using var client = InternalClient();
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("CreateLocationBatch", new
            {
                warehouse_id = _warehouseId,
                parent_id = missingParent,
                type = "rack",
                locations = new[]
                {
                    new { id = childId, code = "A-01", parent_id = missingParent }
                }
            }),
            Json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        body!.Results[0].Outcome.ShouldBe("Rejected");
        body.Results[0].Code.ShouldBe("unknown_parent");

        await using var db = OpenDb();
        (await db.Locations.CountAsync(l => l.WarehouseId == _warehouseId && !l.IsSystem)).ShouldBe(0);
        (await db.Deviations.CountAsync(d => d.CommandId == body.Results[0].CommandId && d.Kind == "rejected")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateLocationBatch_HappyPath_KeepsClientIds()
    {
        var aisleId = Guid.CreateVersion7();
        var result = await Apply("CreateLocationBatch", new
        {
            warehouse_id = _warehouseId,
            parent_id = (Guid?)null,
            type = "aisle",
            locations = new[] { new { id = aisleId, code = "A", parent_id = (Guid?)null } }
        });
        result.Outcome.ShouldBe("Applied");

        await using var db = OpenDb();
        var row = await db.Locations.SingleAsync(l => l.Id == aisleId);
        row.Code.ShouldBe("A");
        row.Type.ShouldBe("aisle");
        row.Path.ShouldBe("A");
        row.Barcode.ShouldBe("A");
    }

    [Fact]
    public async Task CreateLocationBatch_IdempotentRetry_ReturnsStoredResult()
    {
        var aisleId = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        var payload = new
        {
            warehouse_id = _warehouseId,
            parent_id = (Guid?)null,
            type = "aisle",
            locations = new[] { new { id = aisleId, code = "B", parent_id = (Guid?)null } }
        };
        var first = await Apply("CreateLocationBatch", payload, commandId: commandId);
        var second = await Apply("CreateLocationBatch", payload, commandId: commandId);
        first.Outcome.ShouldBe("Applied");
        second.Outcome.ShouldBe("Applied");
        second.CommandId.ShouldBe(commandId);

        await using var db = OpenDb();
        (await db.Locations.CountAsync(l => l.Code == "B" && l.WarehouseId == _warehouseId)).ShouldBe(1);
        (await db.ChangeLog.CountAsync(c => c.CommandId == commandId)).ShouldBe(1);
    }

    [Fact]
    public async Task CreateLocationBatch_SameCodeSameParent_RemapsId()
    {
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Aisle(firstId, "C"))).Outcome.ShouldBe("Applied");
        var remapped = await Apply("CreateLocationBatch", Aisle(secondId, "C"));
        remapped.Outcome.ShouldBe("Applied");
        remapped.Details.ValueKind.ShouldBe(JsonValueKind.Object);
        var map = remapped.Details.GetProperty("id_map");
        map.GetArrayLength().ShouldBe(1);
        map[0].GetProperty("from").GetGuid().ShouldBe(secondId);
        map[0].GetProperty("to").GetGuid().ShouldBe(firstId);

        await using var db = OpenDb();
        (await db.Locations.CountAsync(l => l.Code == "C" && l.WarehouseId == _warehouseId)).ShouldBe(1);
        (await db.Locations.AnyAsync(l => l.Id == firstId)).ShouldBeTrue();
        (await db.Locations.AnyAsync(l => l.Id == secondId)).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateLocationBatch_ActivatedHardCap_Held()
    {
        var warehouseId = Guid.CreateVersion7();
        using var client = InternalClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
        (await client.PostAsJsonAsync("/warehouses", new { id = warehouseId, name = "Cap" }, Json))
            .EnsureSuccessStatusCode();
        GrantMap(warehouseId);
        (await client.PostAsync($"/warehouses/{warehouseId}/activate", null)).EnsureSuccessStatusCode();
        InvalidateEntitlement(0);

        var result = await Apply("CreateLocationBatch", new
        {
            warehouse_id = warehouseId,
            parent_id = (Guid?)null,
            type = "floor",
            locations = new[]
            {
                new { id = Guid.CreateVersion7(), code = "A-01-01-01", parent_id = (Guid?)null }
            }
        });
        result.Outcome.ShouldBe("Held");
        result.Code.ShouldBe("location_cap");

        await using var db = OpenDb();
        (await db.Locations.CountAsync(l => l.WarehouseId == warehouseId && !l.IsSystem)).ShouldBe(0);
        (await db.ProcessedCommands.CountAsync(p => p.CommandId == result.CommandId)).ShouldBe(0);
    }

    [Fact]
    public async Task CreateLocationBatch_DraftIgnoresHardCap()
    {
        var warehouseId = Guid.CreateVersion7();
        using var client = InternalClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
        (await client.PostAsJsonAsync("/warehouses", new { id = warehouseId, name = "Draft" }, Json))
            .EnsureSuccessStatusCode();
        GrantMap(warehouseId);
        InvalidateEntitlement(0);
        var id = Guid.CreateVersion7();
        var result = await Apply("CreateLocationBatch", new
        {
            warehouse_id = warehouseId,
            parent_id = (Guid?)null,
            type = "floor",
            locations = new[] { new { id, code = "A-01-01-02", parent_id = (Guid?)null } }
        });
        result.Outcome.ShouldBe("Applied");
        await using var db = OpenDb();
        (await db.Locations.AnyAsync(l => l.Id == id)).ShouldBeTrue();
    }

    [Fact]
    public async Task CreateLocationBatch_Viewer_Forbidden()
    {
        var result = await Apply("CreateLocationBatch", Aisle(Guid.CreateVersion7(), "D"), userId: _viewerId);
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("forbidden");
    }

    [Fact]
    public async Task CreateLocationBatch_OccurredAtOutsideMembership_Rejected()
    {
        var early = DateTimeOffset.Parse("2019-01-01Z");
        var result = await Apply("CreateLocationBatch", Aisle(Guid.CreateVersion7(), "E"), occurredAt: early);
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("forbidden");
    }

    [Fact]
    public async Task CreateLocationBatch_UnknownWarehouse_Rejected()
    {
        var result = await Apply("CreateLocationBatch", new
        {
            warehouse_id = Guid.CreateVersion7(),
            parent_id = (Guid?)null,
            type = "aisle",
            locations = new[] { new { id = Guid.CreateVersion7(), code = "A", parent_id = (Guid?)null } }
        });
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("unknown_warehouse");
    }

    [Fact]
    public async Task CreateLocationBatch_InvalidCode_Rejected()
    {
        var result = await Apply("CreateLocationBatch", Aisle(Guid.CreateVersion7(), "A-01"));
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("invalid_code");
    }

    [Fact]
    public async Task CreateLocationBatch_StructuralConflict_Rejected()
    {
        (await Apply("CreateLocationBatch", Aisle(Guid.CreateVersion7(), "F"))).Outcome.ShouldBe("Applied");
        var result = await Apply("CreateLocationBatch", new
        {
            warehouse_id = _warehouseId,
            parent_id = (Guid?)null,
            type = "zone",
            locations = new[] { new { id = Guid.CreateVersion7(), code = "F", parent_id = (Guid?)null } }
        });
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("structural_conflict");
    }

    [Fact]
    public async Task SetLocationDimensions_UnknownId_Rejected()
    {
        var missing = Guid.CreateVersion7();
        var result = await Apply("SetLocationDimensions", new
        {
            ids = new[] { missing },
            height_mm = 1200,
            width_mm = 800,
            depth_mm = 400,
            max_weight_g = 500000
        });
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("unknown_location");

        await using var db = OpenDb();
        (await db.Deviations.CountAsync(d => d.CommandId == result.CommandId && d.Kind == "rejected")).ShouldBe(1);
    }

    [Fact]
    public async Task SetLocationDimensions_HappyPath_WritesDims()
    {
        var aisleId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Aisle(aisleId, "G"))).Outcome.ShouldBe("Applied");
        var result = await Apply("SetLocationDimensions", Dims(aisleId, 1200, 800, 400, 500000));
        result.Outcome.ShouldBe("Applied");

        await using var db = OpenDb();
        var row = await db.Locations.SingleAsync(l => l.Id == aisleId);
        row.HeightMm.ShouldBe(1200);
        row.WidthMm.ShouldBe(800);
        row.DepthMm.ShouldBe(400);
        row.MaxWeightG.ShouldBe(500000);
        (await db.ChangeLog.CountAsync(c => c.CommandId == result.CommandId && c.Entity == "location")).ShouldBe(1);
    }

    [Fact]
    public async Task SetLocationDimensions_IdempotentRetry_ReturnsStoredResult()
    {
        var aisleId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Aisle(aisleId, "H"))).Outcome.ShouldBe("Applied");
        var commandId = Guid.CreateVersion7();
        var payload = Dims(aisleId, 1100, 700, 350, 400000);
        var first = await Apply("SetLocationDimensions", payload, commandId: commandId);
        var second = await Apply("SetLocationDimensions", payload, commandId: commandId);
        first.Outcome.ShouldBe("Applied");
        second.Outcome.ShouldBe("Applied");
        second.CommandId.ShouldBe(commandId);

        await using var db = OpenDb();
        (await db.ChangeLog.CountAsync(c => c.CommandId == commandId)).ShouldBe(1);
    }

    [Fact]
    public async Task SetLocationDimensions_LastWriteWins()
    {
        var aisleId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Aisle(aisleId, "I"))).Outcome.ShouldBe("Applied");
        (await Apply("SetLocationDimensions", Dims(aisleId, 1200, null, null, null))).Outcome.ShouldBe("Applied");
        (await Apply("SetLocationDimensions", Dims(aisleId, 1400, null, null, null))).Outcome.ShouldBe("Applied");

        await using var db = OpenDb();
        (await db.Locations.SingleAsync(l => l.Id == aisleId)).HeightMm.ShouldBe(1400);
    }

    [Fact]
    public async Task SetLocationDimensions_OccurredAtOutsideMembership_Rejected()
    {
        var aisleId = Guid.CreateVersion7();
        (await Apply("CreateLocationBatch", Aisle(aisleId, "J"))).Outcome.ShouldBe("Applied");
        var early = DateTimeOffset.Parse("2019-01-01Z");
        var result = await Apply("SetLocationDimensions", Dims(aisleId, 1200, null, null, null), occurredAt: early);
        result.Outcome.ShouldBe("Rejected");
        result.Code.ShouldBe("forbidden");
    }

    [Fact]
    public async Task SetLocationDimensions_SystemLocation_Applies()
    {
        await using var db = OpenDb();
        var receiving = await db.Locations.SingleAsync(l => l.WarehouseId == _warehouseId && l.Code == "RECEIVING");
        var result = await Apply("SetLocationDimensions", Dims(receiving.Id, 500, null, null, null));
        result.Outcome.ShouldBe("Applied");
        await using var again = OpenDb();
        (await again.Locations.SingleAsync(l => l.Id == receiving.Id)).HeightMm.ShouldBe(500);
    }

    private static object Dims(Guid id, int? height, int? width, int? depth, int? weight) => new
    {
        ids = new[] { id },
        height_mm = height,
        width_mm = width,
        depth_mm = depth,
        max_weight_g = weight
    };

    private object Aisle(Guid id, string code) => new
    {
        warehouse_id = _warehouseId,
        parent_id = (Guid?)null,
        type = "aisle",
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
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody(type, payload, userId, commandId, occurredAt),
            Json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        return body!.Results[0];
    }

    private void GrantMap(Guid warehouseId) =>
        _factory.Platform.Memberships.Add(new MembershipAssignment(
            _workerId, "floor_worker", warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null));

    private void InvalidateEntitlement(int hardCap)
    {
        _factory.Platform.Entitlement = new(_tenantId, "Trialing", false, "pro", hardCap);
        _factory.Services.GetRequiredService<IEntitlementCache>().Invalidate(_tenantId);
    }

    private TenantDbContext OpenDb()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new TenantDbContext(options);
    }

    private object CommandBody(
        string type,
        object payload,
        Guid? userId = null,
        Guid? commandId = null,
        DateTimeOffset? occurredAt = null)
    {
        var at = occurredAt ?? _clock.UtcNow;
        return new
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
        };
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
