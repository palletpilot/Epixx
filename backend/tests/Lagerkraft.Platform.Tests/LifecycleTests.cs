using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.Platform.Auth;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Email;
using Lagerkraft.Platform.Lifecycle;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.Platform.Tests;

public sealed class LifecycleTransitionTests
{
    [Fact]
    public void Sp0_AllowedTransitions_SucceedEnsure()
    {
        foreach (var (from, to) in TenantStateMachine.Sp0Transitions)
        {
            Should.NotThrow(() => TenantStateMachine.EnsureAllowed(from, to));
            TenantStateMachine.IsAllowed(from, to).ShouldBeTrue();
        }
    }

    [Fact]
    public void Sp0_DisallowedWiredPairs_ThrowNotImplemented()
    {
        // Active -> PastDue exists in the spec but is not wired in SP0.
        Should.Throw<NotImplementedException>(() =>
            TenantStateMachine.EnsureAllowed(LifecycleState.Active, LifecycleState.PastDue));
        Should.Throw<NotImplementedException>(() =>
            TenantStateMachine.EnsureAllowed(LifecycleState.TrialExpired, LifecycleState.Offboarding));
    }

    [Fact]
    public void Sp0_ImpossiblePair_ThrowsInvalidOperation()
    {
        Should.Throw<InvalidOperationException>(() =>
            TenantStateMachine.EnsureAllowed(LifecycleState.Deleted, LifecycleState.Provisioning));
    }

    [Fact]
    public void NextClosedWindow_WeekdayHours_UsesCloseTime()
    {
        // Wednesday 2026-09-09 12:00 UTC = 14:00 Stockholm (CEST).
        var after = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var hours = OperatingHours.Parse(
            """{"open":"06:00","close":"22:00","days":[1,2,3,4,5,6]}""",
            nightShift: false);
        var flip = OperatingHours.NextClosedWindowStart(hours, after);
        // 22:00 Stockholm = 20:00 UTC
        flip.ShouldBe(new DateTimeOffset(2026, 9, 9, 20, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextClosedWindow_NightShift_UsesNextSixAmLocal()
    {
        var after = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var hours = OperatingHours.Parse(null, nightShift: true);
        var flip = OperatingHours.NextClosedWindowStart(hours, after);
        // Next 06:00 Stockholm after 14:00 local Wed = Thu 06:00 CEST = 04:00 UTC
        flip.ShouldBe(new DateTimeOffset(2026, 9, 10, 4, 0, 0, TimeSpan.Zero));
    }
}

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LifecycleTests : IAsyncLifetime
{
    public const string Password = "Passw0rd!";

    private readonly PlatformApiFactory _factory;

    public LifecycleTests(PostgresFixture postgres) => _factory = new PlatformApiFactory(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task TrialExpiry_LandsAtNextClosedWindow_AfterWarning()
    {
        _factory.Clock.Set(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var seeded = await SeedTrialingAsync(
            trialEndsAt: new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero),
            operatingHours: """{"open":"06:00","close":"22:00","days":[1,2,3,4,5,6]}""",
            nightShift: false);

        await TickExpiryAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await db.Tenants.SingleAsync(t => t.Id == seeded.TenantId);
            tenant.LifecycleState.ShouldBe(LifecycleState.Trialing);
            (await db.AuditLogs.CountAsync(a => a.TenantId == seeded.TenantId && a.Kind == "trial_expiry_warning"))
                .ShouldBe(1);
        }

        _factory.Email.Sent.Any(e => e.To == seeded.Email && e.Subject.Contains("trial", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue();

        _factory.Clock.Set(new DateTimeOffset(2026, 9, 9, 20, 0, 1, TimeSpan.Zero));
        await TickExpiryAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await db.Tenants.SingleAsync(t => t.Id == seeded.TenantId);
            tenant.LifecycleState.ShouldBe(LifecycleState.TrialExpired);
            (await db.AuditLogs.AnyAsync(a =>
                a.TenantId == seeded.TenantId
                && a.Kind == "state_transition"
                && a.ToState == nameof(LifecycleState.TrialExpired))).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task TrialExpiry_NightShift_FlipsAtNextSixAm()
    {
        _factory.Clock.Set(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var seeded = await SeedTrialingAsync(
            trialEndsAt: new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero),
            operatingHours: null,
            nightShift: true);

        _factory.Clock.Set(new DateTimeOffset(2026, 9, 10, 4, 0, 1, TimeSpan.Zero));
        await TickExpiryAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        (await db.Tenants.SingleAsync(t => t.Id == seeded.TenantId)).LifecycleState
            .ShouldBe(LifecycleState.TrialExpired);
    }

    [Fact]
    public async Task ConvertTrial_Owner_MovesToActive()
    {
        _factory.Clock.Set(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        var seeded = await SeedTrialingAsync(
            trialEndsAt: _factory.Clock.UtcNow.AddDays(10),
            operatingHours: null,
            nightShift: false);

        using var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, seeded.Email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var convert = await client.PostAsJsonAsync("/lifecycle/convert-trial", new ConvertTrialRequest(
            PlanCode: "start",
            BillingInterval: "monthly",
            LegalName: "Device Co AB",
            OrgNumber: "5566778899",
            BillingEmail: seeded.Email,
            AddressLine1: "Storgatan 1",
            PostalCode: "11122",
            City: "Stockholm",
            Country: "SE",
            VatNumber: null));
        convert.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Id == seeded.TenantId);
        tenant.LifecycleState.ShouldBe(LifecycleState.Active);
        var sub = await db.Subscriptions.SingleAsync(s => s.TenantId == seeded.TenantId && s.EndedAt == null);
        sub.Status.ShouldBe("active");
        var plan = await db.Plans.SingleAsync(p => p.Id == sub.PlanId);
        plan.Code.ShouldBe("start");
    }

    [Fact]
    public async Task SetMaintenance_TogglesFlag()
    {
        var seeded = await SeedTrialingAsync(_factory.Clock.UtcNow.AddDays(10), null, false);
        using var client = _factory.CreateClient();
        var tokens = await LoginAsync(client, seeded.Email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        (await client.PostAsJsonAsync("/lifecycle/maintenance", new SetMaintenanceRequest(true, "migrate_failed")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            (await db.Tenants.SingleAsync(t => t.Id == seeded.TenantId)).Maintenance.ShouldBeTrue();
        }

        (await client.PostAsJsonAsync("/lifecycle/maintenance", new SetMaintenanceRequest(false, "repaired")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task TickExpiryAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var job = ActivatorUtilities.CreateInstance<TrialExpiryJob>(scope.ServiceProvider);
        await job.TickForTests(CancellationToken.None);
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

    private async Task<Seeded> SeedTrialingAsync(DateTimeOffset trialEndsAt, string? operatingHours, bool nightShift)
    {
        _ = _factory.Services;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var email = $"life-{Ids.New():N}@test.se";
        var user = new AppUser { Id = Ids.New(), Email = email, UserName = email, EmailConfirmed = true };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        var tenant = new Tenant
        {
            Id = Ids.New(),
            Slug = "l" + Ids.New().ToString("N")[^12..],
            CompanyName = "Life Co",
            LifecycleState = LifecycleState.Trialing,
            StateChangedAt = clock.UtcNow,
            OperatingHours = operatingHours,
            NightShift = nightShift,
            OwnerUserId = user.Id
        };
        db.Tenants.Add(tenant);
        db.Memberships.Add(new Membership
        {
            Id = Ids.New(),
            UserId = user.Id,
            TenantId = tenant.Id,
            IsOwner = true
        });
        var plan = db.Plans.AsEnumerable().Single(p => p.Code == "pro");
        db.Subscriptions.Add(new Subscription
        {
            Id = Ids.New(),
            TenantId = tenant.Id,
            PlanId = plan.Id,
            Status = "trialing",
            TrialEndsAt = trialEndsAt,
            StartedAt = trialEndsAt.AddDays(-30)
        });
        db.BillingAccounts.Add(new BillingAccount
        {
            Id = Ids.New(),
            TenantId = tenant.Id,
            LegalName = "Life Co",
            OrgNumber = Ids.New().ToString("N")[..10],
            Country = "SE"
        });
        await db.SaveChangesAsync();
        return new Seeded(user.Id, email, tenant.Id);
    }

    private sealed record Seeded(Guid UserId, string Email, Guid TenantId);
}
