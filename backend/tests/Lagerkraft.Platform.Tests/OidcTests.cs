using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Auth.Oidc;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Tenancy;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.Platform.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OidcTests : IAsyncLifetime
{
    private readonly PlatformApiFactory _factory;
    private FakeOidcIssuer? _issuer;
    private string? _workerEmail;

    public OidcTests(PostgresFixture postgres) => _factory = new PlatformApiFactory(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_issuer is not null)
        {
            await _issuer.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task JitOff_UnknownUser_Refused()
    {
        var (tenantId, _) = await SeedTenantWithProviderAsync("off", null);
        using var client = _factory.CreateClient();
        var state = await StartAsync(client, tenantId);
        var idToken = _issuer!.IssueIdToken(
            Ids.New().ToString("N"),
            $"new-{Ids.New():N}@acme.test",
            emailVerified: true);

        var callback = await client.PostAsJsonAsync(
            "/auth/oidc/callback",
            new OidcCallbackRequest(state, idToken, null));
        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await callback.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().ShouldBe("jit_off");
    }

    [Fact]
    public async Task JitMapped_GrantsMappedRole()
    {
        var mappings = """{"Warehouse.Admin":"warehouse_manager"}""";
        var (tenantId, _) = await SeedTenantWithProviderAsync("mapped", mappings);
        using var client = _factory.CreateClient();
        var state = await StartAsync(client, tenantId);
        var email = $"mapped-{Ids.New():N}@acme.test";
        var idToken = _issuer!.IssueIdToken(
            Ids.New().ToString("N"),
            email,
            emailVerified: true,
            roles: ["Warehouse.Admin"]);

        var callback = await client.PostAsJsonAsync(
            "/auth/oidc/callback",
            new OidcCallbackRequest(state, idToken, null));
        callback.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email);
        user.ShouldNotBeNull();
        var role = await db.RoleAssignments
            .Where(a => a.Membership.UserId == user.Id && a.Membership.TenantId == tenantId)
            .Select(a => a.Role.InternalName)
            .SingleAsync();
        role.ShouldBe(Permissions.WarehouseManager);
    }

    [Fact]
    public async Task Linking_UnverifiedEmail_Refused()
    {
        var (tenantId, ownerEmail) = await SeedTenantWithProviderAsync("off", null);
        using var client = _factory.CreateClient();
        var state = await StartAsync(client, tenantId);
        var idToken = _issuer!.IssueIdToken(Ids.New().ToString("N"), ownerEmail, emailVerified: false);

        var callback = await client.PostAsJsonAsync(
            "/auth/oidc/callback",
            new OidcCallbackRequest(state, idToken, null));
        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await callback.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().ShouldBe("email_unverified");
    }

    [Fact]
    public async Task Enforced_BlocksPasswordForNonOwner_AllowsOwnerWithTotp()
    {
        var (tenantId, ownerEmail) = await SeedTenantWithProviderAsync("off", null, LifecycleState.Active);
        await SeedWorkerAndEnforceAsync(tenantId, ownerEmail);

        using var client = _factory.CreateClient();
        var workerLogin = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest(_workerEmail!, AuthTests.Password, null));
        workerLogin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var code = await CurrentTotpAsync(ownerEmail);
        var ownerLogin = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest(ownerEmail, AuthTests.Password, code));
        ownerLogin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GroupsOverflow_FailsWithSpecMessage()
    {
        var (tenantId, _) = await SeedTenantWithProviderAsync("mapped", """{"g1":"viewer"}""");
        using var client = _factory.CreateClient();
        var state = await StartAsync(client, tenantId);
        var idToken = _issuer!.IssueIdToken(
            Ids.New().ToString("N"),
            $"overflow-{Ids.New():N}@acme.test",
            emailVerified: true,
            groupsOverflow: true);

        var callback = await client.PostAsJsonAsync(
            "/auth/oidc/callback",
            new OidcCallbackRequest(state, idToken, null));
        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await callback.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().ShouldBe(OidcProvisioner.GroupsOverflowMessage);
    }

    private async Task SeedWorkerAndEnforceAsync(Guid tenantId, string ownerEmail)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var owner = await users.FindByEmailAsync(ownerEmail);
        owner.ShouldNotBeNull();
        await users.ResetAuthenticatorKeyAsync(owner);
        await users.SetTwoFactorEnabledAsync(owner, true);

        _workerEmail = $"worker-{Ids.New():N}@acme.test";
        var worker = new AppUser
        {
            Id = Ids.New(),
            Email = _workerEmail,
            UserName = _workerEmail,
            EmailConfirmed = true
        };
        (await users.CreateAsync(worker, AuthTests.Password)).Succeeded.ShouldBeTrue();
        var role = db.Roles.Single(r => r.InternalName == Permissions.FloorWorker);
        var membership = new Membership
        {
            Id = Ids.New(),
            UserId = worker.Id,
            TenantId = tenantId
        };
        db.Memberships.Add(membership);
        db.RoleAssignments.Add(new RoleAssignment
        {
            Id = Ids.New(),
            MembershipId = membership.Id,
            RoleId = role.Id,
            ValidFrom = _factory.Clock.UtcNow
        });
        db.AuditLogs.Add(new AuditLog
        {
            Id = Ids.New(),
            TenantId = tenantId,
            Kind = "sso_login",
            Actor = owner.Id.ToString(),
            At = _factory.Clock.UtcNow
        });
        db.IdentityProviders.Single(p => p.TenantId == tenantId).Enforced = true;
        await db.SaveChangesAsync();
    }

    private async Task<string> CurrentTotpAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email);
        user.ShouldNotBeNull();
        var key = await users.GetAuthenticatorKeyAsync(user);
        key.ShouldNotBeNullOrEmpty();
        return TotpCode(key);
    }

    private static string TotpCode(string base32Key)
    {
        var key = FromBase32(base32Key);
        var timestep = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        Span<byte> data = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data, timestep);
        var hash = HMACSHA1.HashData(key, data);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var trimmed = input.AsSpan().TrimEnd('=');
        var output = new byte[trimmed.Length * 5 / 8];
        var bitIndex = 0;
        var inputIndex = 0;
        var outputBits = 0;
        var outputIndex = 0;
        while (outputIndex < output.Length)
        {
            var byteIndex = alphabet.IndexOf(char.ToUpperInvariant(trimmed[inputIndex]));
            var bits = Math.Min(5 - bitIndex, 8 - outputBits);
            output[outputIndex] <<= bits;
            output[outputIndex] |= (byte)(byteIndex >> (5 - (bitIndex + bits)));
            bitIndex += bits;
            if (bitIndex >= 5)
            {
                inputIndex++;
                bitIndex = 0;
            }

            outputBits += bits;
            if (outputBits >= 8)
            {
                outputIndex++;
                outputBits = 0;
            }
        }

        return output;
    }

    private async Task<(Guid TenantId, string OwnerEmail)> SeedTenantWithProviderAsync(
        string jit,
        string? roleMappings,
        LifecycleState lifecycle = LifecycleState.Trialing)
    {
        _issuer ??= new FakeOidcIssuer();
        await _issuer.StartAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionStringProtector>();

        var ownerEmail = $"owner-{Ids.New():N}@acme.test";
        var owner = new AppUser
        {
            Id = Ids.New(),
            Email = ownerEmail,
            UserName = ownerEmail,
            EmailConfirmed = true
        };
        (await users.CreateAsync(owner, AuthTests.Password)).Succeeded.ShouldBeTrue();

        var tenant = new Tenant
        {
            Id = Ids.New(),
            Slug = "oidc-" + Ids.New().ToString("N")[..10],
            CompanyName = "OIDC Co",
            LifecycleState = lifecycle
        };
        db.Tenants.Add(tenant);
        var admin = db.Roles.Single(r => r.InternalName == Permissions.TenantAdmin);
        var membership = new Membership
        {
            Id = Ids.New(),
            UserId = owner.Id,
            TenantId = tenant.Id,
            IsOwner = true
        };
        db.Memberships.Add(membership);
        db.RoleAssignments.Add(new RoleAssignment
        {
            Id = Ids.New(),
            MembershipId = membership.Id,
            RoleId = admin.Id,
            ValidFrom = _factory.Clock.UtcNow
        });
        db.IdentityProviders.Add(new IdentityProvider
        {
            Id = Ids.New(),
            TenantId = tenant.Id,
            Type = "oidc",
            Issuer = _issuer.Issuer,
            ClientId = _issuer.ClientId,
            ClientSecret = protector.Protect(_issuer.ClientSecret),
            DomainHint = "acme.test",
            JitProvisioning = jit,
            DefaultRole = Permissions.Viewer,
            RoleMappings = roleMappings
        });
        await db.SaveChangesAsync();
        return (tenant.Id, ownerEmail);
    }

    private async Task<string> StartAsync(HttpClient client, Guid tenantId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var slug = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Slug).SingleAsync();
        var start = await client.PostAsJsonAsync("/auth/oidc/start", new OidcStartRequest(null, slug));
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await start.Content.ReadFromJsonAsync<OidcStartResponse>())!.State;
    }
}
