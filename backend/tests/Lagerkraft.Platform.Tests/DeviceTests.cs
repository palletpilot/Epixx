using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Devices;
using Lagerkraft.Platform.Internal;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.Platform.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DeviceTests : IAsyncLifetime
{
    public const string Password = "Passw0rd!";

    private readonly PlatformApiFactory _factory;

    public DeviceTests(PostgresFixture postgres) => _factory = new PlatformApiFactory(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task EnrollmentCode_ExpiresAfterFifteenMinutes()
    {
        var seeded = await SeedManagerAsync();
        using var client = await AuthedClientAsync(seeded);

        var created = await client.PostAsJsonAsync("/devices/enrollment-codes", new CreateEnrollmentCodeRequest(null));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = await created.Content.ReadFromJsonAsync<EnrollmentCodeResponse>();
        code.ShouldNotBeNull();

        _factory.Clock.Advance(TimeSpan.FromMinutes(16));

        var enroll = await client.PostAsJsonAsync(
            "/devices/enroll",
            new EnrollDeviceRequest(code.Code, "Floor 1", null));
        enroll.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RevokedDevice_BeaconReturns410_AndCannotReEnrollSameId()
    {
        var seeded = await SeedManagerAsync();
        using var client = await AuthedClientAsync(seeded);

        var created = await client.PostAsJsonAsync("/devices/enrollment-codes", new CreateEnrollmentCodeRequest(null));
        var code = await created.Content.ReadFromJsonAsync<EnrollmentCodeResponse>();
        code.ShouldNotBeNull();

        var enroll = await client.PostAsJsonAsync(
            "/devices/enroll",
            new EnrollDeviceRequest(code.Code, "Scanner", null));
        enroll.StatusCode.ShouldBe(HttpStatusCode.OK);
        var device = await enroll.Content.ReadFromJsonAsync<EnrollDeviceResponse>();
        device.ShouldNotBeNull();

        using (var internalClient = InternalClient())
        {
            var okBeacon = await internalClient.PostAsJsonAsync(
                $"/internal/devices/{device.DeviceId}/beacon",
                new DeviceBeaconRequest(2, _factory.Clock.UtcNow.AddMinutes(-5), _factory.Clock.UtcNow, true));
            okBeacon.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            var get = await internalClient.GetFromJsonAsync<InternalDeviceResponse>(
                $"/internal/devices/{device.DeviceId}");
            get.ShouldNotBeNull();
            get.PendingCount.ShouldBe(2);
            get.RevokedAt.ShouldBeNull();
        }

        var revoke = await client.PostAsync($"/devices/{device.DeviceId}/revoke", null);
        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using (var internalClient = InternalClient())
        {
            var gone = await internalClient.PostAsJsonAsync(
                $"/internal/devices/{device.DeviceId}/beacon",
                new DeviceBeaconRequest(0, null, _factory.Clock.UtcNow, false));
            gone.StatusCode.ShouldBe(HttpStatusCode.Gone);

            var get = await internalClient.GetFromJsonAsync<InternalDeviceResponse>(
                $"/internal/devices/{device.DeviceId}");
            get.ShouldNotBeNull();
            get.RevokedAt.ShouldNotBeNull();
        }

        var code2 = await (await client.PostAsJsonAsync(
            "/devices/enrollment-codes", new CreateEnrollmentCodeRequest(null)))
            .Content.ReadFromJsonAsync<EnrollmentCodeResponse>();
        code2.ShouldNotBeNull();

        var reEnroll = await client.PostAsJsonAsync(
            "/devices/enroll",
            new EnrollDeviceRequest(code2.Code, "Scanner again", device.DeviceId));
        reEnroll.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Remove_WithPendingWithoutConfirmLoss_IsRefused()
    {
        var seeded = await SeedManagerAsync();
        using var client = await AuthedClientAsync(seeded);

        var code = await (await client.PostAsJsonAsync(
            "/devices/enrollment-codes", new CreateEnrollmentCodeRequest(null)))
            .Content.ReadFromJsonAsync<EnrollmentCodeResponse>();
        var device = await (await client.PostAsJsonAsync(
            "/devices/enroll",
            new EnrollDeviceRequest(code!.Code, "Pending box", null)))
            .Content.ReadFromJsonAsync<EnrollDeviceResponse>();
        device.ShouldNotBeNull();

        using (var internalClient = InternalClient())
        {
            (await internalClient.PostAsJsonAsync(
                $"/internal/devices/{device.DeviceId}/beacon",
                new DeviceBeaconRequest(3, _factory.Clock.UtcNow, _factory.Clock.UtcNow, null)))
                .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var refused = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/devices/{device.DeviceId}")
        {
            Content = JsonContent.Create(new RemoveDeviceRequest(false))
        });
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var confirmed = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/devices/{device.DeviceId}")
        {
            Content = JsonContent.Create(new RemoveDeviceRequest(true))
        });
        confirmed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using (var internalClient = InternalClient())
        {
            (await internalClient.GetAsync($"/internal/devices/{device.DeviceId}"))
                .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Enroll_WithWarehouseRestriction_ReturnsThoseIds_AndListShowsBeacon()
    {
        var seeded = await SeedManagerAsync();
        using var client = await AuthedClientAsync(seeded);
        var warehouses = new[] { seeded.WarehouseId };

        var code = await (await client.PostAsJsonAsync(
            "/devices/enrollment-codes",
            new CreateEnrollmentCodeRequest(warehouses)))
            .Content.ReadFromJsonAsync<EnrollmentCodeResponse>();

        var enroll = await client.PostAsJsonAsync(
            "/devices/enroll",
            new EnrollDeviceRequest(code!.Code, "WH1 pad", null));
        enroll.StatusCode.ShouldBe(HttpStatusCode.OK);
        var device = await enroll.Content.ReadFromJsonAsync<EnrollDeviceResponse>();
        device.ShouldNotBeNull();
        device.WarehouseIds.ShouldBe(warehouses);
        device.TenantId.ShouldBe(seeded.TenantId);
        device.DeviceSecret.ShouldNotBeNullOrEmpty();

        var oldest = _factory.Clock.UtcNow.AddHours(-2);
        using (var internalClient = InternalClient())
        {
            (await internalClient.PostAsJsonAsync(
                $"/internal/devices/{device.DeviceId}/beacon",
                new DeviceBeaconRequest(1, oldest, _factory.Clock.UtcNow, true)))
                .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var list = await client.GetFromJsonAsync<List<DeviceListItem>>("/devices/");
        list.ShouldNotBeNull();
        var row = list.Single(d => d.Id == device.DeviceId);
        row.PendingCount.ShouldBe(1);
        row.OldestPendingOccurredAt.ShouldBe(oldest);
        row.LastSyncAt.ShouldBe(_factory.Clock.UtcNow);
    }

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", PlatformApiFactory.InternalToken);
        return client;
    }

    private async Task<HttpClient> AuthedClientAsync(Seeded seeded)
    {
        var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, seeded.Email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<TokenResponse> LoginAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, Password, null));
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        if (body.TryGetProperty("chooser_token", out var chooser))
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var user = db.Users.AsEnumerable().Single(u => u.Email == email);
            var tenantId = db.Memberships.AsEnumerable().Where(m => m.UserId == user.Id).OrderBy(m => m.CreatedAt).First().TenantId;
            var chosen = await client.PostAsJsonAsync(
                "/auth/choose-tenant",
                new ChooseTenantRequest(chooser.GetString()!, tenantId));
            chosen.EnsureSuccessStatusCode();
            return (await chosen.Content.ReadFromJsonAsync<TokenResponse>())!;
        }

        return body.Deserialize<TokenResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<Seeded> SeedManagerAsync()
    {
        _ = _factory.Services;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var email = $"dev-{Ids.New():N}@test.se";
        var user = new AppUser { Id = Ids.New(), Email = email, UserName = email, EmailConfirmed = true };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        var tenant = new Tenant
        {
            Id = Ids.New(),
            Slug = "d" + Ids.New().ToString("N")[^12..],
            CompanyName = "Device Co",
            LifecycleState = LifecycleState.Trialing
        };
        db.Tenants.Add(tenant);

        var role = db.Roles.AsEnumerable().Single(r => r.InternalName == Permissions.WarehouseManager);
        var warehouseId = Ids.New();
        var membership = new Membership
        {
            Id = Ids.New(),
            UserId = user.Id,
            TenantId = tenant.Id,
            IsOwner = true
        };
        db.Memberships.Add(membership);
        db.RoleAssignments.Add(new Lagerkraft.Platform.Data.RoleAssignment
        {
            Id = Ids.New(),
            MembershipId = membership.Id,
            RoleId = role.Id,
            WarehouseId = warehouseId,
            ValidFrom = clock.UtcNow
        });
        await db.SaveChangesAsync();
        return new Seeded(user.Id, email, tenant.Id, warehouseId);
    }

    private sealed record Seeded(Guid UserId, string Email, Guid TenantId, Guid WarehouseId);
}
