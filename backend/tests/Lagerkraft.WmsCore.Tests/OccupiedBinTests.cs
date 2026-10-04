using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OccupiedBinTests : IAsyncLifetime
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
    private Guid _userId;
    private Guid _deviceId;
    private Guid _warehouseId;
    private string _cs = "";

    public OccupiedBinTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _userId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _warehouseId = Guid.CreateVersion7();
        _cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, _tenantId);
        _factory.Platform.ConnectionString = _cs;
        _factory.Platform.Entitlement = new(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.Memberships =
        [
            new MembershipAssignment(_userId, "warehouse_worker", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null)
        ];
        _factory.Clock = _clock;
        using var client = InternalClient();
        (await client.PostAsync($"/internal/tenants/{_tenantId}/migrate", null)).EnsureSuccessStatusCode();

        client.DefaultRequestHeaders.Remove("Lagerkraft-Tenant-Id");
        client.DefaultRequestHeaders.Add("Lagerkraft-Tenant-Id", _tenantId.ToString());
        var wh = await client.PostAsJsonAsync("/warehouses", new
        {
            id = _warehouseId,
            name = "WH1",
            claim_minutes = 30
        }, Json);
        wh.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task ConfirmPutaway_OccupiedBin_DoesNotCreateSecondStockRow()
    {
        var locationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var existingHuId = Guid.CreateVersion7();
        var incomingHuId = Guid.CreateVersion7();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);

        db.Locations.Add(new Location
        {
            Id = locationId,
            WarehouseId = _warehouseId,
            Code = "A-01-01",
            Type = "bin"
        });

        db.HandlingUnits.Add(new HandlingUnit
        {
            Id = existingHuId,
            WarehouseId = _warehouseId,
            Lpn = "PALLET001",
            LocationId = locationId
        });

        db.Stock.Add(new Stock
        {
            Id = Ids.New(),
            WarehouseId = _warehouseId,
            LocationId = locationId,
            HandlingUnitId = existingHuId,
            ArticleId = null,
            QtyBase = 1m,
            CreatedAt = _clock.UtcNow
        });

        db.Tasks.Add(new WarehouseTask
        {
            Id = taskId,
            WarehouseId = _warehouseId,
            Type = "putaway",
            Status = "open",
            CreatedAt = _clock.UtcNow
        });

        await db.SaveChangesAsync();

        using var client = InternalClient();
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("ConfirmPutaway", 1, new
            {
                task_id = taskId,
                location_id = locationId,
                handling_unit_id = incomingHuId,
                lpn = "PALLET002"
            }, _clock.UtcNow, _userId),
            Json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        body!.Results[0].Outcome.ShouldBe("Applied");

        var stockRows = await db.Stock.Where(s => s.LocationId == locationId).ToListAsync();
        stockRows.Count.ShouldBe(1);
        stockRows[0].HandlingUnitId.ShouldBe(incomingHuId);

        var deviations = await db.Deviations.Where(d => d.Kind == "occupied_bin").ToListAsync();
        deviations.Count.ShouldBe(1);
        deviations[0].Detail.ShouldContain(existingHuId.ToString());
        deviations[0].Detail.ShouldContain(incomingHuId.ToString());
    }

    [Fact]
    public async Task ConfirmPutaway_OccupiedBin_IncomingPalletIsOnlyOccupant()
    {
        var locationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var existingHuId = Guid.CreateVersion7();
        var incomingHuId = Guid.CreateVersion7();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);

        db.Locations.Add(new Location
        {
            Id = locationId,
            WarehouseId = _warehouseId,
            Code = "A-01-01",
            Type = "bin"
        });

        db.HandlingUnits.Add(new HandlingUnit
        {
            Id = existingHuId,
            WarehouseId = _warehouseId,
            Lpn = "PALLET001",
            LocationId = locationId
        });

        db.Stock.Add(new Stock
        {
            Id = Ids.New(),
            WarehouseId = _warehouseId,
            LocationId = locationId,
            HandlingUnitId = existingHuId,
            ArticleId = null,
            QtyBase = 1m,
            CreatedAt = _clock.UtcNow
        });

        db.Tasks.Add(new WarehouseTask
        {
            Id = taskId,
            WarehouseId = _warehouseId,
            Type = "putaway",
            Status = "open",
            CreatedAt = _clock.UtcNow
        });

        await db.SaveChangesAsync();

        using var client = InternalClient();
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("ConfirmPutaway", 1, new
            {
                task_id = taskId,
                location_id = locationId,
                handling_unit_id = incomingHuId,
                lpn = "PALLET002"
            }, _clock.UtcNow, _userId),
            Json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        db.ChangeTracker.Clear();
        var handlingUnits = await db.HandlingUnits.Where(hu => hu.LocationId == locationId).ToListAsync();
        handlingUnits.Count.ShouldBe(1);
        handlingUnits[0].Id.ShouldBe(incomingHuId);
        handlingUnits[0].Lpn.ShouldBe("PALLET002");
    }

    [Fact]
    public async Task ConfirmPutaway_OccupiedBin_AppliedPlusOneDeviation()
    {
        var locationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var existingHuId = Guid.CreateVersion7();
        var incomingHuId = Guid.CreateVersion7();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);

        db.Locations.Add(new Location
        {
            Id = locationId,
            WarehouseId = _warehouseId,
            Code = "A-01-01",
            Type = "bin"
        });

        db.HandlingUnits.Add(new HandlingUnit
        {
            Id = existingHuId,
            WarehouseId = _warehouseId,
            Lpn = "PALLET001",
            LocationId = locationId
        });

        var existingStockId = Ids.New();
        db.Stock.Add(new Stock
        {
            Id = existingStockId,
            WarehouseId = _warehouseId,
            LocationId = locationId,
            HandlingUnitId = existingHuId,
            ArticleId = null,
            QtyBase = 1m,
            CreatedAt = _clock.UtcNow
        });

        db.Tasks.Add(new WarehouseTask
        {
            Id = taskId,
            WarehouseId = _warehouseId,
            Type = "putaway",
            Status = "open",
            CreatedAt = _clock.UtcNow
        });

        await db.SaveChangesAsync();

        using var client = InternalClient();
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("ConfirmPutaway", 1, new
            {
                task_id = taskId,
                location_id = locationId,
                handling_unit_id = incomingHuId,
                lpn = "PALLET002"
            }, _clock.UtcNow, _userId),
            Json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        body!.Results[0].Outcome.ShouldBe("Applied");

        var stockCount = await db.Stock.CountAsync(s => s.LocationId == locationId);
        stockCount.ShouldBe(1);

        var deviationCount = await db.Deviations.CountAsync(d => d.Kind == "occupied_bin");
        deviationCount.ShouldBe(1);

        var stockDelete = await db.ChangeLog
            .FirstOrDefaultAsync(c => c.Entity == "stock" && c.Id == existingStockId && c.Op == "delete");
        stockDelete.ShouldNotBeNull();

        var deviationInsert = await db.ChangeLog
            .FirstOrDefaultAsync(c => c.Entity == "deviation" && c.Op == "insert");
        deviationInsert.ShouldNotBeNull();
        deviationInsert.CommandId.ShouldBe(stockDelete.CommandId);

        db.ChangeTracker.Clear();
        var previousHu = await db.HandlingUnits.FirstOrDefaultAsync(h => h.Id == existingHuId);
        previousHu.ShouldNotBeNull();
        previousHu.LocationId.ShouldBeNull();
    }

    [Fact]
    public async Task ConfirmPutaway_OccupiedBin_PreviousOccupantInChangeFeed()
    {
        var locationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var existingHuId = Guid.CreateVersion7();
        var incomingHuId = Guid.CreateVersion7();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);

        db.Locations.Add(new Location
        {
            Id = locationId,
            WarehouseId = _warehouseId,
            Code = "A-01-01",
            Type = "bin"
        });

        db.HandlingUnits.Add(new HandlingUnit
        {
            Id = existingHuId,
            WarehouseId = _warehouseId,
            Lpn = "PALLET001",
            LocationId = locationId
        });

        var existingStockId = Ids.New();
        db.Stock.Add(new Stock
        {
            Id = existingStockId,
            WarehouseId = _warehouseId,
            LocationId = locationId,
            HandlingUnitId = existingHuId,
            ArticleId = null,
            QtyBase = 1m,
            CreatedAt = _clock.UtcNow
        });

        db.Tasks.Add(new WarehouseTask
        {
            Id = taskId,
            WarehouseId = _warehouseId,
            Type = "putaway",
            Status = "open",
            CreatedAt = _clock.UtcNow
        });

        await db.SaveChangesAsync();

        using var client = InternalClient();
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("ConfirmPutaway", 1, new
            {
                task_id = taskId,
                location_id = locationId,
                handling_unit_id = incomingHuId,
                lpn = "PALLET002"
            }, _clock.UtcNow, _userId),
            Json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var deleteEntry = await db.ChangeLog
            .FirstOrDefaultAsync(c => c.Entity == "stock" && c.Id == existingStockId && c.Op == "delete");
        deleteEntry.ShouldNotBeNull();

        var upsertEntry = await db.ChangeLog
            .FirstOrDefaultAsync(c => c.Entity == "stock" && c.Id != existingStockId && c.Op == "upsert");
        upsertEntry.ShouldNotBeNull();
    }

    [Fact]
    public async Task ConfirmPutaway_OccupiedBin_OneDeviationChangeWithWarehouseId()
    {
        var locationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var existingHuId = Guid.CreateVersion7();
        var incomingHuId = Guid.CreateVersion7();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);

        db.Locations.Add(new Location
        {
            Id = locationId,
            WarehouseId = _warehouseId,
            Code = "A-01-01",
            Type = "bin"
        });

        db.HandlingUnits.Add(new HandlingUnit
        {
            Id = existingHuId,
            WarehouseId = _warehouseId,
            Lpn = "PALLET001",
            LocationId = locationId
        });

        var existingStockId = Ids.New();
        db.Stock.Add(new Stock
        {
            Id = existingStockId,
            WarehouseId = _warehouseId,
            LocationId = locationId,
            HandlingUnitId = existingHuId,
            ArticleId = null,
            QtyBase = 1m,
            CreatedAt = _clock.UtcNow
        });

        db.Tasks.Add(new WarehouseTask
        {
            Id = taskId,
            WarehouseId = _warehouseId,
            Type = "putaway",
            Status = "open",
            CreatedAt = _clock.UtcNow
        });

        await db.SaveChangesAsync();

        using var client = InternalClient();
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("ConfirmPutaway", 1, new
            {
                task_id = taskId,
                location_id = locationId,
                handling_unit_id = incomingHuId,
                lpn = "PALLET002"
            }, _clock.UtcNow, _userId),
            Json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var deviationChanges = await db.ChangeLog
            .Where(c => c.Entity == "deviation" && c.Op == "insert")
            .ToListAsync();
        deviationChanges.Count.ShouldBe(1);

        var deviationChange = deviationChanges[0];
        var payload = JsonDocument.Parse(deviationChange.Payload);
        payload.RootElement.TryGetProperty("warehouse_id", out var warehouseIdProp).ShouldBeTrue();
        payload.RootElement.GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);

        var stockDelete = await db.ChangeLog
            .FirstOrDefaultAsync(c => c.Entity == "stock" && c.Id == existingStockId && c.Op == "delete");
        stockDelete.ShouldNotBeNull();
        stockDelete.WarehouseId.ShouldBe(_warehouseId);
        deviationChange.CommandId.ShouldBe(stockDelete.CommandId);
    }

    [Fact]
    public async Task ConfirmPutaway_OccupiedBin_ChangesFeedFiltersByWarehouse()
    {
        var locationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var existingHuId = Guid.CreateVersion7();
        var incomingHuId = Guid.CreateVersion7();
        var otherWarehouseId = Guid.CreateVersion7();
        var otherChangeId = Guid.CreateVersion7();
        var nullWarehouseChangeId = Guid.CreateVersion7();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);

        db.Locations.Add(new Location
        {
            Id = locationId,
            WarehouseId = _warehouseId,
            Code = "A-01-01",
            Type = "bin"
        });

        db.HandlingUnits.Add(new HandlingUnit
        {
            Id = existingHuId,
            WarehouseId = _warehouseId,
            Lpn = "PALLET001",
            LocationId = locationId
        });

        var existingStockId = Ids.New();
        db.Stock.Add(new Stock
        {
            Id = existingStockId,
            WarehouseId = _warehouseId,
            LocationId = locationId,
            HandlingUnitId = existingHuId,
            ArticleId = null,
            QtyBase = 1m,
            CreatedAt = _clock.UtcNow
        });

        db.Tasks.Add(new WarehouseTask
        {
            Id = taskId,
            WarehouseId = _warehouseId,
            Type = "putaway",
            Status = "open",
            CreatedAt = _clock.UtcNow
        });

        await db.SaveChangesAsync();

        using var client = InternalClient();
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("ConfirmPutaway", 1, new
            {
                task_id = taskId,
                location_id = locationId,
                handling_unit_id = incomingHuId,
                lpn = "PALLET002"
            }, _clock.UtcNow, _userId),
            Json);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = "stock",
            Id = otherChangeId,
            Op = "upsert",
            Payload = "{}",
            WarehouseId = otherWarehouseId,
            OccurredAt = _clock.UtcNow,
            RecordedAt = _clock.UtcNow
        });
        db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = "probe",
            Id = nullWarehouseChangeId,
            Op = "upsert",
            Payload = "{}",
            WarehouseId = null,
            OccurredAt = _clock.UtcNow,
            RecordedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync();

        var feed = await client.GetAsync(
            $"/internal/changes?tenantId={_tenantId}&warehouse={_warehouseId}");
        feed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await feed.Content.ReadFromJsonAsync<JsonDocument>(Json);
        var entries = body!.RootElement.GetProperty("entries").EnumerateArray().ToList();

        entries.ShouldNotBeEmpty();
        foreach (var entry in entries)
        {
            entry.GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);
            entry.GetProperty("id").GetGuid().ShouldNotBe(otherChangeId);
            entry.GetProperty("id").GetGuid().ShouldNotBe(nullWarehouseChangeId);
        }

        var stockDeleteEntry = entries.Single(e =>
            e.GetProperty("entity").GetString() == "stock"
            && e.GetProperty("id").GetGuid() == existingStockId
            && e.GetProperty("op").GetString() == "delete");
        stockDeleteEntry.GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);
        stockDeleteEntry.GetProperty("payload").GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);

        var deviationChanges = await db.ChangeLog
            .Where(c => c.Entity == "deviation" && c.Op == "insert")
            .ToListAsync();
        deviationChanges.Count.ShouldBe(1);
        var payload = JsonDocument.Parse(deviationChanges[0].Payload);
        payload.RootElement.GetProperty("warehouse_id").GetGuid().ShouldBe(_warehouseId);

        var stockDelete = await db.ChangeLog
            .FirstOrDefaultAsync(c => c.Entity == "stock" && c.Id == existingStockId && c.Op == "delete");
        stockDelete.ShouldNotBeNull();
        stockDelete.WarehouseId.ShouldBe(_warehouseId);
        deviationChanges[0].CommandId.ShouldBe(stockDelete.CommandId);
    }

    [Fact]
    public async Task DeviationSnapshot_FiltersByWarehouse()
    {
        var warehouse1 = Guid.CreateVersion7();
        var warehouse2 = Guid.CreateVersion7();
        var deviation1 = Guid.CreateVersion7();
        var deviation2 = Guid.CreateVersion7();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);

        db.Deviations.Add(new Deviation
        {
            Id = deviation1,
            WarehouseId = warehouse1,
            Kind = "occupied_bin",
            CommandId = Guid.CreateVersion7(),
            Detail = "{}",
            CreatedAt = _clock.UtcNow
        });

        db.Deviations.Add(new Deviation
        {
            Id = deviation2,
            WarehouseId = warehouse2,
            Kind = "occupied_bin",
            CommandId = Guid.CreateVersion7(),
            Detail = "{}",
            CreatedAt = _clock.UtcNow
        });

        await db.SaveChangesAsync();

        using var client = InternalClient();
        var response = await client.GetAsync($"/internal/snapshot?entity=deviation&warehouse={warehouse1}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonDocument>(Json);
        var items = body!.RootElement.GetProperty("items").EnumerateArray().ToList();

        items.Count.ShouldBe(1);
        items[0].GetProperty("id").GetGuid().ShouldBe(deviation1);
        items[0].GetProperty("warehouse_id").GetGuid().ShouldBe(warehouse1);
    }

    private object CommandBody(
        string type,
        int v,
        object payload,
        DateTimeOffset now,
        Guid userId,
        DateTimeOffset? occurredAt = null,
        Guid? deviceId = null) =>
        new
        {
            tenant_id = _tenantId,
            now,
            commands = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    type,
                    v,
                    payload = JsonSerializer.SerializeToElement(payload, Json),
                    occurred_at = occurredAt ?? now,
                    device_id = deviceId ?? _deviceId,
                    user_id = userId
                }
            }
        };

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        return client;
    }

    private sealed record BatchResultDto(List<CommandResultDto> Results, int? MinAppVersion, bool UpgradeRequired);
    private sealed record CommandResultDto(Guid CommandId, string Outcome, string? Code, string? Message);
}
