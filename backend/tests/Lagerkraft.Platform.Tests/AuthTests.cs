using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.Platform.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthTests : IAsyncLifetime
{
    public const string Password = "Passw0rd!";
    public const string Pin = "1234";

    private readonly PlatformApiFactory _factory;

    public AuthTests(PostgresFixture postgres) => _factory = new PlatformApiFactory(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Login_TwoWarehouseManager_TokenHasClaims()
    {
        var seeded = await SeedManagerAsync();

        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest(seeded.Email, Password, null));
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var chooser = await login.Content.ReadFromJsonAsync<ChooserResponse>();
        chooser.ShouldNotBeNull();
        var chosen = await client.PostAsJsonAsync(
            "/auth/choose-tenant",
            new ChooseTenantRequest(chooser.ChooserToken, seeded.TenantA));
        chosen.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await chosen.Content.ReadFromJsonAsync<TokenResponse>();
        tokens.ShouldNotBeNull();

        var jwt = Payload(tokens.AccessToken);
        jwt.GetProperty("sub").GetString().ShouldBe(seeded.UserId.ToString());
        jwt.GetProperty(LagerkraftClaims.TenantId).GetString().ShouldBe(seeded.TenantA.ToString());
        jwt.GetProperty(LagerkraftClaims.AuthMethod).GetString().ShouldBe("pwd");
        jwt.GetProperty(LagerkraftClaims.Owner).GetString().ShouldBe("true");
        jwt.GetProperty(LagerkraftClaims.PermissionMapVersion).GetString().ShouldBe(Permissions.MapVersion.ToString());
        jwt.GetProperty(LagerkraftClaims.SessionVersion).GetString().ShouldBe("0");
        using var doc = JsonDocument.Parse(jwt.GetProperty(LagerkraftClaims.RoleAssignments).GetString()!);
        var row = doc.RootElement[0];
        row.GetProperty("r").GetString().ShouldBe(Permissions.WarehouseManager);
        var warehouses = row.GetProperty("w").EnumerateArray().Select(v => Guid.Parse(v.GetString()!)).ToArray();
        warehouses.ShouldBe([seeded.Warehouse1, seeded.Warehouse2], ignoreOrder: true);
        jwt.TryGetProperty(LagerkraftClaims.DeviceId, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Login_WithEnrolledDevice_SkipsChooserAndPutsDevClaim()
    {
        var seeded = await SeedManagerAsync();
        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            email = seeded.Email,
            password = Password,
            totp = (string?)null,
            device_id = seeded.DeviceId
        });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await login.Content.ReadFromJsonAsync<TokenResponse>();
        tokens.ShouldNotBeNull();
        tokens.AccessToken.ShouldNotBeNullOrEmpty();

        var jwt = Payload(tokens.AccessToken);
        jwt.GetProperty("sub").GetString().ShouldBe(seeded.UserId.ToString());
        jwt.GetProperty(LagerkraftClaims.TenantId).GetString().ShouldBe(seeded.TenantA.ToString());
        jwt.GetProperty(LagerkraftClaims.DeviceId).GetString().ShouldBe(seeded.DeviceId.ToString());
        jwt.GetProperty("dev").GetString().ShouldBe(seeded.DeviceId.ToString());
        jwt.GetProperty(LagerkraftClaims.AuthMethod).GetString().ShouldBe("pwd");
    }

    [Fact]
    public async Task Login_UnknownDevice_Unauthorized()
    {
        var seeded = await SeedManagerAsync();
        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            email = seeded.Email,
            password = Password,
            totp = (string?)null,
            device_id = Ids.New()
        });
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_AfterSessionVersionBump_Fails()
    {
        var seeded = await SeedManagerAsync();
        using var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, seeded.Email);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionVersionBump>()
                .BumpForUserAsync(seeded.UserId, seeded.TenantA, CancellationToken.None);
        }

        var refresh = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PinUnlock_FiveFailures_LockedFifteenMinutes()
    {
        var seeded = await SeedManagerAsync();
        using var client = _factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var wrong = await client.PostAsJsonAsync(
                "/auth/pin-unlock",
                new PinUnlockRequest(seeded.DeviceId, seeded.UserId, "0000"));
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var locked = await client.PostAsJsonAsync(
            "/auth/pin-unlock",
            new PinUnlockRequest(seeded.DeviceId, seeded.UserId, Pin));
        locked.StatusCode.ShouldBe(HttpStatusCode.Locked);

        _factory.Clock.Advance(TimeSpan.FromMinutes(15));
        var unlocked = await client.PostAsJsonAsync(
            "/auth/pin-unlock",
            new PinUnlockRequest(seeded.DeviceId, seeded.UserId, Pin));
        unlocked.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await unlocked.Content.ReadFromJsonAsync<TokenResponse>();
        var jwt = Payload(tokens!.AccessToken);
        jwt.GetProperty(LagerkraftClaims.AuthMethod).GetString().ShouldBe("pin");
        jwt.GetProperty(LagerkraftClaims.DeviceId).GetString().ShouldBe(seeded.DeviceId.ToString());
    }

    [Fact]
    public async Task PinUnlock_TenFailures_RequiresFullLogin()
    {
        var seeded = await SeedManagerAsync();
        using var client = _factory.CreateClient();

        await FailPinAsync(client, seeded, 5);
        _factory.Clock.Advance(TimeSpan.FromMinutes(15));
        await FailPinAsync(client, seeded, 5);

        var refused = await client.PostAsJsonAsync(
            "/auth/pin-unlock",
            new PinUnlockRequest(seeded.DeviceId, seeded.UserId, Pin));
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await LoginAsync(client, seeded.Email)).AccessToken.ShouldNotBeNullOrEmpty();

        var afterLogin = await client.PostAsJsonAsync(
            "/auth/pin-unlock",
            new PinUnlockRequest(seeded.DeviceId, seeded.UserId, Pin));
        afterLogin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TotpEnrollThenLogin_ValidCode_Succeeds()
    {
        var seeded = await SeedManagerAsync();
        using var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, seeded.Email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var enroll = await client.PostAsync("/auth/totp/enroll", null);
        enroll.StatusCode.ShouldBe(HttpStatusCode.OK);

        var code = await CurrentAuthenticatorCodeAsync(seeded.Email);
        var confirm = await client.PostAsJsonAsync("/auth/totp/enroll/confirm", new TotpConfirmRequest(code));
        confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        client.DefaultRequestHeaders.Authorization = null;
        var withCode = await LoginAsync(client, seeded.Email, code);
        var jwt = Payload(withCode.AccessToken);
        jwt.GetProperty(LagerkraftClaims.AuthMethod).GetString().ShouldBe("mfa");
    }

    [Fact]
    public async Task TotpLogin_WrongCode_DoesNotSucceed()
    {
        var seeded = await SeedManagerAsync();
        using var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, seeded.Email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        await client.PostAsync("/auth/totp/enroll", null);
        var code = await CurrentAuthenticatorCodeAsync(seeded.Email);
        await client.PostAsJsonAsync("/auth/totp/enroll/confirm", new TotpConfirmRequest(code));
        client.DefaultRequestHeaders.Authorization = null;

        var wrong = await client.PostAsJsonAsync("/auth/login", new LoginRequest(seeded.Email, Password, "000000"));
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SwitchTenant_SecondMembership_IssuesToken()
    {
        var seeded = await SeedManagerAsync();
        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest(seeded.Email, Password, null));
        var chooser = await login.Content.ReadFromJsonAsync<ChooserResponse>();
        chooser.ShouldNotBeNull();
        chooser.Memberships.Select(m => m.TenantId).ShouldBe([seeded.TenantA, seeded.TenantB], ignoreOrder: true);

        var chosen = await client.PostAsJsonAsync(
            "/auth/choose-tenant",
            new ChooseTenantRequest(chooser.ChooserToken, seeded.TenantA));
        chosen.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await chosen.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var switched = await client.PostAsJsonAsync("/auth/switch-tenant", new SwitchTenantRequest(seeded.TenantB));
        switched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var next = Payload((await switched.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);
        next.GetProperty(LagerkraftClaims.TenantId).GetString().ShouldBe(seeded.TenantB.ToString());
    }

    [Fact]
    public async Task SwitchTenant_NotAMember_Refused()
    {
        var seeded = await SeedManagerAsync();
        using var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, seeded.Email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var other = await SeedBareTenantAsync();
        var refused = await client.PostAsJsonAsync("/auth/switch-tenant", new SwitchTenantRequest(other));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static JsonElement Payload(string accessToken)
    {
        var part = accessToken.Split('.')[1];
        var padded = part.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
        return doc.RootElement.Clone();
    }

    private async Task FailPinAsync(HttpClient client, SeededUser seeded, int times)
    {
        for (var i = 0; i < times; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/auth/pin-unlock",
                new PinUnlockRequest(seeded.DeviceId, seeded.UserId, "0000"));
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    private async Task<TokenResponse> LoginAsync(HttpClient client, string email, string? totp = null)
    {
        var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, Password, totp));
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

    private async Task<string> CurrentAuthenticatorCodeAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email);
        user.ShouldNotBeNull();
        var key = await users.GetAuthenticatorKeyAsync(user);
        key.ShouldNotBeNullOrEmpty();
        // Identity 10's AuthenticatorTokenProvider.GenerateAsync is a no-op (codes are not sent).
        return TotpCode(key);
    }

    private async Task<SeededUser> SeedManagerAsync()
    {
        _ = _factory.Services;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var email = $"mgr-{Ids.New():N}@test.se";
        var user = new AppUser { Id = Ids.New(), Email = email, UserName = email, EmailConfirmed = true };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        var tenantA = NewTenant("a");
        var tenantB = NewTenant("b");
        db.Tenants.AddRange(tenantA, tenantB);

        var role = db.Roles.AsEnumerable().Single(r => r.InternalName == Permissions.WarehouseManager);
        var warehouse1 = Ids.New();
        var warehouse2 = Ids.New();
        var membershipA = new Membership
        {
            Id = Ids.New(),
            UserId = user.Id,
            TenantId = tenantA.Id,
            IsOwner = true,
            PinHash = PinHasher.Hash(Pin)
        };
        var membershipB = new Membership
        {
            Id = Ids.New(),
            UserId = user.Id,
            TenantId = tenantB.Id
        };
        db.Memberships.AddRange(membershipA, membershipB);
        db.RoleAssignments.AddRange(
            new Lagerkraft.Platform.Data.RoleAssignment
            {
                Id = Ids.New(),
                MembershipId = membershipA.Id,
                RoleId = role.Id,
                WarehouseId = warehouse1,
                ValidFrom = clock.UtcNow
            },
            new Lagerkraft.Platform.Data.RoleAssignment
            {
                Id = Ids.New(),
                MembershipId = membershipA.Id,
                RoleId = role.Id,
                WarehouseId = warehouse2,
                ValidFrom = clock.UtcNow
            },
            new Lagerkraft.Platform.Data.RoleAssignment
            {
                Id = Ids.New(),
                MembershipId = membershipB.Id,
                RoleId = role.Id,
                WarehouseId = null,
                ValidFrom = clock.UtcNow
            });

        var device = new Device
        {
            Id = Ids.New(),
            TenantId = tenantA.Id,
            Name = "Scanner 1"
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return new SeededUser(user.Id, email, tenantA.Id, tenantB.Id, warehouse1, warehouse2, device.Id);
    }

    private async Task<Guid> SeedBareTenantAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = NewTenant("x");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static string TotpCode(string base32Key)
    {
        var key = FromBase32(base32Key);
        var timestep = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        Span<byte> data = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(data, timestep);
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
        if (trimmed.Length == 0)
        {
            return [];
        }

        var output = new byte[trimmed.Length * 5 / 8];
        var bitIndex = 0;
        var inputIndex = 0;
        var outputBits = 0;
        var outputIndex = 0;
        while (outputIndex < output.Length)
        {
            var byteIndex = alphabet.IndexOf(char.ToUpperInvariant(trimmed[inputIndex]));
            byteIndex.ShouldBeGreaterThanOrEqualTo(0);
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

    private static Tenant NewTenant(string suffix) => new()
    {
        Id = Ids.New(),
        Slug = "t" + suffix + Ids.New().ToString("N")[^12..],
        CompanyName = "Co " + suffix,
        LifecycleState = LifecycleState.Trialing
    };

    private sealed record SeededUser(
        Guid UserId,
        string Email,
        Guid TenantA,
        Guid TenantB,
        Guid Warehouse1,
        Guid Warehouse2,
        Guid DeviceId);
}
