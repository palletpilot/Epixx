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
public sealed class TaskModuleTests : IAsyncLifetime
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
    private Guid _viewerId;
    private Guid _deviceId;
    private Guid _warehouseId;
    private string _cs = "";

    public TaskModuleTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _userId = Guid.CreateVersion7();
        _viewerId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _warehouseId = Guid.CreateVersion7();
        _cs = await TenantDatabases.CreateAsync(_postgres.ConnectionString, _tenantId);
        _factory.Platform.ConnectionString = _cs;
        _factory.Platform.Entitlement = new(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.Memberships =
        [
            new MembershipAssignment(_userId, "warehouse_worker", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null),
            new MembershipAssignment(_viewerId, "viewer", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null)
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
    public async Task ClaimTask_TwentyConcurrent_ExactlyOneApplied()
    {
        var taskId = await CreateOpenTask();
        using var client = InternalClient();
        var now = _clock.UtcNow;
        var users = Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7()).ToList();
        foreach (var u in users)
        {
            _factory.Platform.Memberships.Add(new MembershipAssignment(
                u, "warehouse_worker", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null));
        }

        var responses = await Task.WhenAll(users.Select(async u =>
        {
            var deviceId = Guid.CreateVersion7();
            var body = CommandBody("ClaimTask", 1, new { task_id = taskId }, now, u, deviceId: deviceId);
            return await client.PostAsJsonAsync("/internal/commands", body, Json);
        }));

        var results = new List<CommandResultDto>();
        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var batch = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
            results.Add(batch!.Results[0]);
        }

        var applied = results.Count(r => r.Outcome == "Applied");
        var summary = string.Join("; ", results.GroupBy(r => r.Outcome + ":" + r.Code).Select(g => $"{g.Key}={g.Count()}"));
        applied.ShouldBe(1, summary);
        results.Count(r => r.Outcome == "Rejected" && r.Code == "already_claimed").ShouldBe(19, summary);
    }

    [Fact]
    public async Task AssignmentSweep_ReleasesExpiredClaim_WritesChangeLog()
    {
        var taskId = await CreateOpenTask();
        using var client = InternalClient();
        var claim = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("ClaimTask", 1, new { task_id = taskId }, _clock.UtcNow, _userId),
            Json);
        claim.EnsureSuccessStatusCode();

        _clock.Advance(TimeSpan.FromMinutes(31));
        var sweep = _factory.Services.GetRequiredService<AssignmentSweepJob>();
        await sweep.SweepTenantAsync(_tenantId, CancellationToken.None);

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_cs).UseSnakeCaseNamingConvention().Options;
        await using var db = new TenantDbContext(options);
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId);
        task.Status.ShouldBe("open");
        task.AssigneeUserId.ShouldBeNull();
        (await db.ChangeLog.CountAsync(c => c.Entity == "task" && c.Id == taskId)).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ClaimTask_Viewer_Forbidden()
    {
        var taskId = await CreateOpenTask();
        using var client = InternalClient();
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("ClaimTask", 1, new { task_id = taskId }, _clock.UtcNow, _viewerId),
            Json);
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        body!.Results[0].Outcome.ShouldBe("Rejected");
        body.Results[0].Code.ShouldBe("forbidden");
    }

    [Fact]
    public async Task ClaimTask_OccurredAtOutsideMembership_Rejected()
    {
        var taskId = await CreateOpenTask();
        using var client = InternalClient();
        var early = DateTimeOffset.Parse("2019-01-01Z");
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("ClaimTask", 1, new { task_id = taskId }, early, _userId, early),
            Json);
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        body!.Results[0].Outcome.ShouldBe("Rejected");
        body.Results[0].Code.ShouldBe("forbidden");
    }

    private async Task<Guid> CreateOpenTask()
    {
        var taskId = Guid.CreateVersion7();
        using var client = InternalClient();
        // elevate: temporarily add manager membership for create
        _factory.Platform.Memberships.Add(new MembershipAssignment(
            _userId, "warehouse_manager", _warehouseId, DateTimeOffset.Parse("2020-01-01Z"), null));
        var response = await client.PostAsJsonAsync(
            "/internal/commands",
            CommandBody("CreateTask", 1, new { id = taskId, warehouse_id = _warehouseId, type = "putaway" }, _clock.UtcNow, _userId),
            Json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BatchResultDto>(Json);
        body!.Results[0].Outcome.ShouldBe("Applied");
        return taskId;
    }

    private object CommandBody(
        string type,
        int v,
        object payload,
        DateTimeOffset now,
        Guid userId,
        DateTimeOffset? occurredAt = null,
        Guid? deviceId = null) =>
        new
        {
            tenant_id = _tenantId,
            now,
            commands = new[]
            {
                new
                {
                    id = Guid.CreateVersion7(),
                    type,
                    v,
                    payload = JsonSerializer.SerializeToElement(payload, Json),
                    occurred_at = occurredAt ?? now,
                    device_id = deviceId ?? _deviceId,
                    user_id = userId
                }
            }
        };

    private HttpClient InternalClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Lagerkraft-Internal-Token", WmsCoreApiFactory.InternalToken);
        return client;
    }

    private sealed record BatchResultDto(List<CommandResultDto> Results, int? MinAppVersion, bool UpgradeRequired);
    private sealed record CommandResultDto(Guid CommandId, string Outcome, string? Code, string? Message);
}



