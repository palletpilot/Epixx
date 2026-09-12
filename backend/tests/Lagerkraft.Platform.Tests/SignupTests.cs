using System.Net;
using System.Net.Http.Json;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Provisioning;
using Lagerkraft.Platform.Signup;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.Platform.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SignupTests : IAsyncLifetime
{
    private readonly PlatformApiFactory _factory;

    public SignupTests(PostgresFixture postgres) => _factory = new PlatformApiFactory(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Signup_Verify_Provision_ReachesTrialing()
    {
        using var client = _factory.CreateClient();
        var org = ValidOrg("556677001");
        var email = $"owner-{Ids.New():N}@acme.se";

        var signup = await client.PostAsJsonAsync("/signup", new SignupRequestBody(
            "Acme Lager",
            org,
            email,
            "Ada Owner",
            AuthTests.Password,
            null,
            false));
        signup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await signup.Content.ReadFromJsonAsync<SignupAcceptedResponse>();
        accepted.ShouldNotBeNull();
        accepted.Slug.ShouldNotBeNullOrEmpty();

        _factory.Email.Sent.ShouldContain(e => e.To == email);
        var token = ExtractToken(_factory.Email.Sent.Last(e => e.To == email).Body);

        var verify = await client.PostAsJsonAsync("/signup/verify", new VerifySignupRequest(token));
        verify.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await TickProvisioningAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var row = await db.SignupRequests.FindAsync(accepted.SignupId);
        row.ShouldNotBeNull();
        row.TenantId.ShouldNotBeNull();
        var tenant = await db.Tenants.FindAsync(row.TenantId!.Value);
        tenant.ShouldNotBeNull();
        tenant.LifecycleState.ShouldBe(LifecycleState.Trialing);
        tenant.Slug.ShouldBe(accepted.Slug);
        (await db.Memberships.CountAsync(m => m.TenantId == tenant.Id && m.IsOwner)).ShouldBe(1);
        (await db.Subscriptions.CountAsync(s => s.TenantId == tenant.Id && s.Status == "trialing")).ShouldBe(1);
        (await db.BillingAccounts.SingleAsync(b => b.TenantId == tenant.Id)).OrgNumber.ShouldBe(OrgNumber.Normalize(org));
        _factory.DatabaseCreator.Calls.ShouldBeGreaterThan(0);
        _factory.MigrateClient.Calls.ShouldBeGreaterThan(0);
        _factory.MigrateClient.DevWarehouseCalls.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Signup_OrgCollision_JoinRequest_RevealsCompanyNameOnly()
    {
        var org = ValidOrg("556677002");
        await SeedLiveTenantWithOrgAsync("Existing Co", org);

        using var client = _factory.CreateClient();
        var withoutFlag = await client.PostAsJsonAsync("/signup", new SignupRequestBody(
            "Anything",
            org,
            $"x-{Ids.New():N}@other.se",
            "X",
            AuthTests.Password,
            null,
            false));
        withoutFlag.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var hint = await withoutFlag.Content.ReadFromJsonAsync<JoinRequestResponse>();
        hint.ShouldNotBeNull();
        hint.CompanyName.ShouldBe("Existing Co");

        var join = await client.PostAsJsonAsync("/signup", new SignupRequestBody(
            "Anything",
            org,
            $"join-{Ids.New():N}@other.se",
            "Joiner",
            AuthTests.Password,
            null,
            true));
        join.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await join.Content.ReadFromJsonAsync<JoinRequestResponse>();
        body.ShouldNotBeNull();
        body.CompanyName.ShouldBe("Existing Co");
        body.SignupId.ShouldNotBe(Guid.Empty);

        var json = await join.Content.ReadAsStringAsync();
        json.ShouldNotContain("slug");
        json.ShouldNotContain("Existing Co\".\"slug", Case.Insensitive);
    }

    [Fact]
    public async Task Provisioning_CreateDatabaseFailsThrice_ProvisioningFailed()
    {
        _factory.DatabaseCreator.Fail = true;
        using var client = _factory.CreateClient();
        var org = ValidOrg("556677003");
        var email = $"fail-{Ids.New():N}@acme.se";
        var signup = await client.PostAsJsonAsync("/signup", new SignupRequestBody(
            "Fail Co", org, email, "F", AuthTests.Password, null, false));
        var accepted = await signup.Content.ReadFromJsonAsync<SignupAcceptedResponse>();
        var token = ExtractToken(_factory.Email.Sent.Last(e => e.To == email).Body);
        await client.PostAsJsonAsync("/signup/verify", new VerifySignupRequest(token));

        await TickProvisioningAsync();
        await TickProvisioningAsync();
        await TickProvisioningAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var row = await db.SignupRequests.FindAsync(accepted!.SignupId);
        row!.ProvisioningAttempts.ShouldBe(3);
        var tenant = await db.Tenants.FindAsync(row.TenantId!.Value);
        tenant!.LifecycleState.ShouldBe(LifecycleState.ProvisioningFailed);
        tenant.LifecycleState.ShouldNotBe(LifecycleState.Trialing);
        _factory.DatabaseCreator.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Provisioning_MigrateServerError_NeverTrialing()
    {
        _factory.MigrateClient.FailWithServerError = true;
        using var client = _factory.CreateClient();
        var org = ValidOrg("556677004");
        var email = $"mig-{Ids.New():N}@acme.se";
        var signup = await client.PostAsJsonAsync("/signup", new SignupRequestBody(
            "Mig Co", org, email, "M", AuthTests.Password, null, false));
        var accepted = await signup.Content.ReadFromJsonAsync<SignupAcceptedResponse>();
        var token = ExtractToken(_factory.Email.Sent.Last(e => e.To == email).Body);
        await client.PostAsJsonAsync("/signup/verify", new VerifySignupRequest(token));

        await TickProvisioningAsync();
        await TickProvisioningAsync();
        await TickProvisioningAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var row = await db.SignupRequests.FindAsync(accepted!.SignupId);
        var tenant = await db.Tenants.FindAsync(row!.TenantId!.Value);
        tenant!.LifecycleState.ShouldBe(LifecycleState.ProvisioningFailed);
    }

    [Fact]
    public async Task PurgeUnverified_OlderThanSevenDays_Deleted()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.SignupRequests.Add(new SignupRequest
            {
                Id = Ids.New(),
                Email = $"old-{Ids.New():N}@acme.se",
                OrgNumber = ValidOrg("556677005"),
                CompanyName = "Old",
                Slug = "old-" + Ids.New().ToString("N")[..8],
                DisplayName = "Old",
                PasswordHash = "x",
                VerificationTokenHash = "y",
                CreatedAt = _factory.Clock.UtcNow.AddDays(-8),
                ExpiresAt = _factory.Clock.UtcNow.AddDays(-7)
            });
            await db.SaveChangesAsync();
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var job = ActivatorUtilities.CreateInstance<PurgeUnverifiedJob>(scope.ServiceProvider);
            await job.TickForTests(CancellationToken.None);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            (await db.SignupRequests.CountAsync(s => s.CompanyName == "Old")).ShouldBe(0);
        }
    }

    private async Task TickProvisioningAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var job = ActivatorUtilities.CreateInstance<ProvisioningJob>(scope.ServiceProvider);
        await job.TickForTests(CancellationToken.None);
    }

    private async Task SeedLiveTenantWithOrgAsync(string company, string org)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = new Tenant
        {
            Id = Ids.New(),
            Slug = "live-" + Ids.New().ToString("N")[..10],
            CompanyName = company,
            LifecycleState = LifecycleState.Trialing
        };
        db.Tenants.Add(tenant);
        db.BillingAccounts.Add(new BillingAccount
        {
            Id = Ids.New(),
            TenantId = tenant.Id,
            LegalName = company,
            OrgNumber = OrgNumber.Normalize(org),
            Country = "SE"
        });
        var email = $"owner-{Ids.New():N}@existing.se";
        db.Users.Add(new AppUser
        {
            Id = Ids.New(),
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        });
        await db.SaveChangesAsync();
        var user = db.Users.Single(u => u.Email == email);
        db.Memberships.Add(new Membership
        {
            Id = Ids.New(),
            UserId = user.Id,
            TenantId = tenant.Id,
            IsOwner = true
        });
        await db.SaveChangesAsync();
    }

    private static string ExtractToken(string body)
    {
        const string marker = "Token: ";
        var idx = body.IndexOf(marker, StringComparison.Ordinal);
        idx.ShouldBeGreaterThanOrEqualTo(0);
        return body[(idx + marker.Length)..].Trim();
    }

    private static string ValidOrg(string nineDigits)
    {
        nineDigits = new string(nineDigits.Where(char.IsDigit).ToArray());
        nineDigits.Length.ShouldBe(9);
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var n = (nineDigits[i] - '0') * (i % 2 == 0 ? 2 : 1);
            sum += n / 10 + n % 10;
        }

        var check = (10 - sum % 10) % 10;
        return nineDigits + check;
    }
}