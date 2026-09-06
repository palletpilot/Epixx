using System.Net;
using System.Net.Http.Json;
using Lagerkraft.Platform.Data;
using Lagerkraft.Platform.Internal;
using Lagerkraft.Platform.Tenancy;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.Platform.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InternalApiTests : IAsyncLifetime
{
    private readonly PlatformApiFactory _factory;

    public InternalApiTests(PostgresFixture postgres) => _factory = new PlatformApiFactory(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task GetConnection_DecryptsStoredCiphertext()
    {
        const string plaintext = "Host=tenant-db;Database=tenant_x;Username=u;Password=secret";
        var tenantId = await SeedTenant(plaintext, "pro");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<TenantCatalog>();
            (await catalog.GetConnectionAsync(tenantId, CancellationToken.None)).ShouldBe(plaintext);
        }

        using var client = InternalClient();
        var body = await client.GetFromJsonAsync<TenantConnectionResponse>(
            $"/internal/tenants/{tenantId}/connection");
        body.ShouldNotBeNull();
        body.ConnectionString.ShouldBe(plaintext);
    }

    [Fact]
    public async Task GetEntitlement_SeededProPlan_Returned()
    {
        var tenantId = await SeedTenant("Host=db;Database=t", "pro");

        using var client = InternalClient();
        var body = await client.GetFromJsonAsync<EntitlementResponse>(
            $"/internal/tenants/{tenantId}/entitlement");
        body.ShouldNotBeNull();
        body.TenantId.ShouldBe(tenantId);
        body.LifecycleState.ShouldBe(nameof(LifecycleState.Trialing));
        body.PlanCode.ShouldBe("pro");
        body.HardCapUnits.ShouldBe(5000);
        body.Features.GetProperty("sso").GetBoolean().ShouldBeTrue();
        body.Features.GetProperty("webhooks").GetBoolean().ShouldBeTrue();
        body.Features.GetProperty("integrations").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task PutMigrationStatus_PersistedOnGet()
    {
        var tenantId = await SeedTenant("Host=db;Database=t", "start");

        using var client = InternalClient();
        var put = await client.PutAsJsonAsync(
            $"/internal/tenants/{tenantId}/migration-status",
            new MigrationStatusRequest("20260906000000_InitialTenant", "up_to_date", null));
        put.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var catalog = await client.GetFromJsonAsync<TenantCatalogResponse>($"/internal/tenants/{tenantId}");
        catalog.ShouldNotBeNull();
        catalog.SchemaVersion.ShouldBe("20260906000000_InitialTenant");
        catalog.MigrationStatus.ShouldBe("up_to_date");
        catalog.LastError.ShouldBeNull();
        catalog.LastAttemptAt.ShouldBe(_factory.Clock.UtcNow);
    }

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", PlatformApiFactory.InternalToken);
        return client;
    }

    private async Task<Guid> SeedTenant(string connectionString, string planCode)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionStringProtector>();
        var plan = db.Plans.AsEnumerable().Single(p => p.Code == planCode);
        var (wrappedDek, ciphertext) = protector.Encrypt(connectionString);
        var tenant = new Tenant
        {
            Id = Ids.New(),
            Slug = "t" + Ids.New().ToString("N")[..12],
            CompanyName = "Acme",
            LifecycleState = LifecycleState.Trialing,
            WrappedDek = wrappedDek,
            ConnectionCiphertext = ciphertext
        };
        db.Tenants.Add(tenant);
        db.Subscriptions.Add(new Subscription
        {
            Id = Ids.New(),
            TenantId = tenant.Id,
            PlanId = plan.Id,
            Status = "trialing",
            BillingInterval = "monthly",
            StartedAt = _factory.Clock.UtcNow
        });
        await db.SaveChangesAsync();
        return tenant.Id;
    }
}
