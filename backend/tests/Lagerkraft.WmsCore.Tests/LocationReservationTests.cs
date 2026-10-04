using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.WmsCore.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LocationReservationTests : IAsyncLifetime
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

    public LocationReservationTests(PostgresFixture postgres) => _postgres = postgres;

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
    public async Task LocationReservationSweep_LiveReservation_LeftAlone()
    {
        var locationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var expiresAt = _clock.UtcNow.AddMinutes(30);

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
        
        db.LocationReservations.Add(new LocationReservation
        {
            LocationId = locationId,
            TaskId = taskId,
            ExpiresAt = expiresAt,
            Released = false
        });
        await db.SaveChangesAsync();

        var sweep = _factory.Services.GetRequiredService<LocationReservationSweepJob>();
        await sweep.SweepTenantAsync(_tenantId, CancellationToken.None);

        db.ChangeTracker.Clear();
        var reservation = await db.LocationReservations.SingleOrDefaultAsync(
            r => r.LocationId == locationId && r.TaskId == taskId);
        reservation.ShouldNotBeNull();
        reservation.ExpiresAt.ShouldBe(expiresAt);
        reservation.Released.ShouldBeFalse();
    }

    [Fact]
    public async Task LocationReservationSweep_ExpiredReservation_Released()
    {
        var locationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var expiresAt = _clock.UtcNow.AddMinutes(-5);

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
        
        db.LocationReservations.Add(new LocationReservation
        {
            LocationId = locationId,
            TaskId = taskId,
            ExpiresAt = expiresAt,
            Released = false
        });
        await db.SaveChangesAsync();

        var sweep = _factory.Services.GetRequiredService<LocationReservationSweepJob>();
        await sweep.SweepTenantAsync(_tenantId, CancellationToken.None);

        db.ChangeTracker.Clear();
        var reservation = await db.LocationReservations.SingleOrDefaultAsync(
            r => r.LocationId == locationId && r.TaskId == taskId);
        reservation.ShouldNotBeNull();
        reservation.Released.ShouldBeTrue();
    }

    [Fact]
    public async Task LocationReservationSweep_SecondSweep_NoOp()
    {
        var locationId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var expiresAt = _clock.UtcNow.AddMinutes(-5);

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
        
        db.LocationReservations.Add(new LocationReservation
        {
            LocationId = locationId,
            TaskId = taskId,
            ExpiresAt = expiresAt,
            Released = false
        });
        await db.SaveChangesAsync();

        var sweep = _factory.Services.GetRequiredService<LocationReservationSweepJob>();
        await sweep.SweepTenantAsync(_tenantId, CancellationToken.None);
        
        db.ChangeTracker.Clear();
        var reservation = await db.LocationReservations.SingleOrDefaultAsync(
            r => r.LocationId == locationId && r.TaskId == taskId);
        reservation.ShouldNotBeNull();
        reservation.Released.ShouldBeTrue();

        await sweep.SweepTenantAsync(_tenantId, CancellationToken.None);

        db.ChangeTracker.Clear();
        var reservationAfterSecond = await db.LocationReservations.SingleOrDefaultAsync(
            r => r.LocationId == locationId && r.TaskId == taskId);
        reservationAfterSecond.ShouldNotBeNull();
        reservationAfterSecond.Released.ShouldBeTrue();
    }

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        return client;
    }
}
