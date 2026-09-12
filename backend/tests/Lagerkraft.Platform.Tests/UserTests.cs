using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Internal;
using Lagerkraft.Platform.Users;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.Platform.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UserTests : IAsyncLifetime
{
    public const string Password = "Passw0rd!";

    private readonly PlatformApiFactory _factory;

    public UserTests(PostgresFixture postgres) => _factory = new PlatformApiFactory(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task InviteThenAccept_YieldsMembershipWithRole()
    {
        var admin = await SeedAdminAsync(Permissions.TenantAdmin);
        using var client = await AuthedClientAsync(admin);

        var invite = await client.PostAsJsonAsync(
            "/users/invitations",
            new CreateInvitationRequest("newbie@test.se", Permissions.FloorWorker, null));
        invite.StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = await invite.Content.ReadFromJsonAsync<InvitationCreatedResponse>();
        created.ShouldNotBeNull();

        client.DefaultRequestHeaders.Authorization = null;
        var accept = await client.PostAsJsonAsync(
            "/users/invitations/accept",
            new AcceptInvitationRequest(created.Token, "NewPassw0rd!"));
        accept.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await accept.Content.ReadFromJsonAsync<AcceptInvitationResponse>();
        accepted.ShouldNotBeNull();
        accepted.Role.ShouldBe(Permissions.FloorWorker);
        accepted.TenantId.ShouldBe(admin.TenantId);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var membership = await db.Memberships.SingleAsync(m => m.UserId == accepted.UserId && m.TenantId == admin.TenantId);
        var roles = await db.RoleAssignments.Where(a => a.MembershipId == membership.Id && a.ValidTo == null)
            .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => r.InternalName)
            .ToListAsync();
        roles.ShouldContain(Permissions.FloorWorker);
    }

    [Fact]
    public async Task ManagerInvite_TenantAdmin_IsRejected()
    {
        var manager = await SeedAdminAsync(Permissions.WarehouseManager);
        using var client = await AuthedClientAsync(manager);

        var invite = await client.PostAsJsonAsync(
            "/users/invitations",
            new CreateInvitationRequest("boss@test.se", Permissions.TenantAdmin, null));
        invite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AssignRole_ClosesPreviousAndHistoryHasBothWindows()
    {
        var admin = await SeedAdminAsync(Permissions.TenantAdmin);
        using var client = await AuthedClientAsync(admin);

        var invite = await (await client.PostAsJsonAsync(
            "/users/invitations",
            new CreateInvitationRequest("rolehop@test.se", Permissions.Viewer, null)))
            .Content.ReadFromJsonAsync<InvitationCreatedResponse>();
        client.DefaultRequestHeaders.Authorization = null;
        var accepted = await (await client.PostAsJsonAsync(
            "/users/invitations/accept",
            new AcceptInvitationRequest(invite!.Token, "NewPassw0rd!")))
            .Content.ReadFromJsonAsync<AcceptInvitationResponse>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await LoginAsync(client, admin.Email)).AccessToken);

        var assign = await client.PostAsJsonAsync(
            $"/users/{accepted!.UserId}/roles",
            new AssignRoleRequest(Permissions.FloorWorker, null));
        assign.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var internalClient = InternalClient();
        var history = await internalClient.GetFromJsonAsync<List<MembershipHistoryResponse>>(
            $"/internal/memberships/{admin.TenantId}/history?userId={accepted.UserId}");
        history.ShouldNotBeNull();
        history.Count.ShouldBeGreaterThanOrEqualTo(2);
        history.Count(h => h.ValidTo is null).ShouldBe(1);
        history.Count(h => h.ValidTo is not null).ShouldBeGreaterThanOrEqualTo(1);
        history.Select(h => h.Role).ShouldContain(Permissions.Viewer);
        history.Select(h => h.Role).ShouldContain(Permissions.FloorWorker);
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
            var tenantId = db.Memberships.AsEnumerable().First(m => m.UserId == user.Id).TenantId;
            var chosen = await client.PostAsJsonAsync(
                "/auth/choose-tenant",
                new ChooseTenantRequest(chooser.GetString()!, tenantId));
            chosen.EnsureSuccessStatusCode();
            return (await chosen.Content.ReadFromJsonAsync<TokenResponse>())!;
        }

        return body.Deserialize<TokenResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<Seeded> SeedAdminAsync(string roleName)
    {
        _ = _factory.Services;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var email = $"u-{Ids.New():N}@test.se";
        var user = new AppUser { Id = Ids.New(), Email = email, UserName = email, EmailConfirmed = true };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        var tenant = new Tenant
        {
            Id = Ids.New(),
            Slug = "u" + Ids.New().ToString("N")[^12..],
            CompanyName = "Users Co",
            LifecycleState = LifecycleState.Trialing,
            OwnerUserId = user.Id
        };
        db.Tenants.Add(tenant);
        var role = db.Roles.AsEnumerable().Single(r => r.InternalName == roleName);
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
            WarehouseId = null,
            ValidFrom = clock.UtcNow
        });
        await db.SaveChangesAsync();
        return new Seeded(user.Id, email, tenant.Id);
    }

    private sealed record Seeded(Guid UserId, string Email, Guid TenantId);
}
