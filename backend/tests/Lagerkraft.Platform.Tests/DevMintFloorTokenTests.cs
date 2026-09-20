using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Dev;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lagerkraft.Platform.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DevMintFloorTokenTests : IAsyncLifetime
{
    public const string Password = "Passw0rd!";

    private readonly PlatformApiFactory _factory;

    public DevMintFloorTokenTests(PostgresFixture postgres) => _factory = new PlatformApiFactory(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task MintFloorToken_TestingEnv_ReturnsJwtWithPinAmrAndDeviceClaim()
    {
        var seeded = await SeedAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/dev/mint-floor-token", new MintFloorTokenRequest(
            seeded.UserId, seeded.TenantId, seeded.DeviceId, "shell"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
        tokens.ShouldNotBeNull();

        var jwt = Payload(tokens.AccessToken);
        jwt.GetProperty("sub").GetString().ShouldBe(seeded.UserId.ToString());
        jwt.GetProperty(LagerkraftClaims.TenantId).GetString().ShouldBe(seeded.TenantId.ToString());
        // Floor device claim short name is "dev" (LagerkraftClaims.DeviceId).
        jwt.GetProperty(LagerkraftClaims.DeviceId).GetString().ShouldBe(seeded.DeviceId.ToString());
        jwt.GetProperty("dev").GetString().ShouldBe(seeded.DeviceId.ToString());
        jwt.GetProperty(LagerkraftClaims.SessionVersion).GetString().ShouldNotBeNullOrEmpty();
        jwt.GetProperty(LagerkraftClaims.AuthMethod).GetString().ShouldBe("pin");
    }

    [Fact]
    public async Task MintFloorToken_ProductionEnv_Returns404()
    {
        await using var prodFactory = _factory.WithWebHostBuilder(b => b.UseEnvironment(Environments.Production));
        using var client = prodFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/dev/mint-floor-token", new MintFloorTokenRequest(
            Ids.New(), Ids.New(), Ids.New(), "shell"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static JsonElement Payload(string accessToken)
    {
        var part = accessToken.Split('.')[1];
        var padded = part.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
        return doc.RootElement.Clone();
    }

    private async Task<Seeded> SeedAsync()
    {
        _ = _factory.Services;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var email = $"mint-{Ids.New():N}@test.se";
        var user = new AppUser { Id = Ids.New(), Email = email, UserName = email, EmailConfirmed = true };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        var tenant = new Tenant
        {
            Id = Ids.New(),
            Slug = "m" + Ids.New().ToString("N")[^12..],
            CompanyName = "Mint Co",
            LifecycleState = LifecycleState.Trialing,
            OwnerUserId = user.Id
        };
        db.Tenants.Add(tenant);
        var role = db.Roles.AsEnumerable().Single(r => r.InternalName == Permissions.FloorWorker);
        var membership = new Membership
        {
            Id = Ids.New(),
            UserId = user.Id,
            TenantId = tenant.Id
        };
        db.Memberships.Add(membership);
        db.RoleAssignments.Add(new Lagerkraft.Platform.Data.RoleAssignment
        {
            Id = Ids.New(),
            MembershipId = membership.Id,
            RoleId = role.Id,
            ValidFrom = clock.UtcNow
        });
        var deviceId = Ids.New();
        await db.SaveChangesAsync();
        return new Seeded(user.Id, tenant.Id, deviceId);
    }

    private sealed record Seeded(Guid UserId, Guid TenantId, Guid DeviceId);
}
