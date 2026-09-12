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
public sealed class CommandPipelineTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly PostgresFixture _postgres;
    private readonly WmsCoreApiFactory _factory = new();
    private Guid _tenantId;
    private Guid _userId;
    private Guid _deviceId;
    private string _cs = "";

    public CommandPipelineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _userId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, _tenantId);
        _factory.Platform.ConnectionString = _cs;
        _factory.Platform.Entitlement = new(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.Memberships =
        [
            new MembershipAssignment(_userId, "warehouse_worker", null, DateTimeOffset.Parse("2020-01-01Z"), null)
        ];
        using var client = InternalClient();
        (await client.PostAsync($"/internal/tenants/{_tenantId}/migrate", null)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Commands_IdempotentRetry_ReturnsStoredResult()
    {
        var cmdId = Guid.CreateVersion7();
        var probeId = Guid.CreateVersion7();
        using var client = InternalClient();

        var first = await PostProbe(client, cmdId, probeId, "hello");
        var second = await PostProbe(client, cmdId, probeId, "hello");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var r1 = await first.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        var r2 = await second.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        r1!.Results[0].Outcome.ShouldBe("Applied");
        r2!.Results[0].Outcome.ShouldBe("Applied");

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);
        (await db.ChangeLog.CountAsync(c => c.CommandId == cmdId)).ShouldBe(1);
    }

    [Fact]
    public async Task Commands_TrialExpired_Held()
    {
        _factory.Platform.Entitlement = new(_tenantId, "TrialExpired", false, "pro", 1000);
        using var client = InternalClient();
        var response = await PostProbe(client, Guid.CreateVersion7(), Guid.CreateVersion7(), "x");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        body!.Results[0].Outcome.ShouldBe("Held");
        body.Results[0].Code.ShouldBe("trial_expired");
    }

    [Fact]
    public async Task Commands_UnknownVersion_Returns426()
    {
        using var client = InternalClient();
        var payload = JsonSerializer.SerializeToElement(new { probe_id = Guid.CreateVersion7(), message = "x" }, Json);
        var now = DateTimeOffset.UtcNow;
        var body = new
        {
            tenant_id = _tenantId,
            now,
            commands = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    type = "Probe",
                    v = 99,
                    payload,
                    occurred_at = now,
                    device_id = _deviceId,
                    user_id = _userId
                }
            }
        };
        var response = await client.PostAsJsonAsync("/internal/commands", body, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.UpgradeRequired);
        var result = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        result!.UpgradeRequired.ShouldBeTrue();
        result.Results[0].Outcome.ShouldBe("Unknown");
    }

    [Fact]
    public async Task Commands_Rejection_WritesDeviation()
    {
        using var client = InternalClient();
        var cmdId = Guid.CreateVersion7();
        var response = await PostProbe(client, cmdId, Guid.CreateVersion7(), "reject-me");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        body!.Results[0].Outcome.ShouldBe("Rejected");

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);
        (await db.Deviations.CountAsync(d => d.CommandId == cmdId && d.Kind == "rejected")).ShouldBe(1);
    }

    [Fact]
    public async Task Commands_FutureOccurredAt_HitsStaleHook()
    {
        using var client = InternalClient();
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToElement(new { probe_id = Guid.CreateVersion7(), message = "x" }, Json);
        var body = new
        {
            tenant_id = _tenantId,
            now,
            commands = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    type = "Probe",
                    v = 1,
                    payload,
                    occurred_at = now.AddMinutes(4),
                    device_id = _deviceId,
                    user_id = _userId
                }
            }
        };
        var response = await client.PostAsJsonAsync("/internal/commands", body, Json);
        var result = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        result!.Results[0].Outcome.ShouldBe("Rejected");
        result.Results[0].Code.ShouldBe("stale_command");
    }

    [Fact]
    public async Task Commands_TwoBatchesSameDevice_Serialize()
    {
        using var client = InternalClient();
        var tasks = Enumerable.Range(0, 2).Select(async i =>
        {
            var cmdId = Guid.CreateVersion7();
            return await PostProbe(client, cmdId, Guid.CreateVersion7(), $"m{i}");
        });
        var responses = await Task.WhenAll(tasks);
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);
        (await db.ProcessedCommands.CountAsync()).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Upcaster_V0ToV1_Applies()
    {
        using var client = InternalClient();
        var cmdId = Guid.CreateVersion7();
        var probeId = Guid.CreateVersion7();
        var payload = JsonSerializer.SerializeToElement(new { probe_id = probeId, text = "legacy" }, Json);
        var now = DateTimeOffset.UtcNow;
        var body = new
        {
            tenant_id = _tenantId,
            now,
            commands = new[]
            {
                new
                {
                    id = cmdId,
                    type = "Probe",
                    v = 0,
                    payload,
                    occurred_at = now,
                    device_id = _deviceId,
                    user_id = _userId
                }
            }
        };
        var response = await client.PostAsJsonAsync("/internal/commands", body, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        result!.Results[0].Outcome.ShouldBe("Applied");
    }

    private async Task<HttpResponseMessage> PostProbe(HttpClient client, Guid cmdId, Guid probeId, string message)
    {
        var payload = JsonSerializer.SerializeToElement(new { probe_id = probeId, message }, Json);
        var now = DateTimeOffset.UtcNow;
        var body = new
        {
            tenant_id = _tenantId,
            now,
            commands = new[]
            {
                new
                {
                    id = cmdId,
                    type = "Probe",
                    v = 1,
                    payload,
                    occurred_at = now,
                    device_id = _deviceId,
                    user_id = _userId
                }
            }
        };
        return await client.PostAsJsonAsync("/internal/commands", body, Json);
    }

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        return client;
    }

    private sealed record BatchResultDto(List<CommandResultDto> Results, int? MinAppVersion, bool UpgradeRequired);
    private sealed record CommandResultDto(Guid CommandId, string Outcome, string? Code, string? Message);
}


