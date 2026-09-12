using System.Security.Claims;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.SyncGateway.Clients;
using Lagerkraft.SyncGateway.Realtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lagerkraft.SyncGateway.Tests;

[Trait("Category", "Integration")]
public sealed class D2RealtimeTests : IAsyncLifetime
{
    private readonly SyncGatewayApiFactory _factory = new();
    private Guid _tenantId;
    private Guid _userId;
    private Guid _deviceId;

    public Task InitializeAsync()
    {
        RealtimeEndpoint.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
        _tenantId = Guid.CreateVersion7();
        _userId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _factory.Platform.Devices[_deviceId] = new InternalDevice(
            _deviceId, _tenantId, "floor-1", [], null, 0);
        _factory.Platform.Entitlement = new TenantEntitlement(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.CurrentSessionVersion = 1;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Realtime_CatchUpThenLive_NoGapsOrDuplicates()
    {
        var e1 = Entry(10, "Task");
        var e2 = Entry(11, "Task");
        var e3 = Entry(12, "Task");
        _factory.Wms.OnGetChanges = () =>
        {
            // Publish live events while catch-up is running (buffered)
            var bus = _factory.Services.GetRequiredService<InMemoryTenantEventBus>();
            _ = bus.PublishAsync(_tenantId, Live(11, e2));
            _ = bus.PublishAsync(_tenantId, Live(12, e3));
            return ChangesBody(e1, e2, e3);
        };

        using var client = Authed(role: Permissions.Viewer);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/realtime?since=9");
        request.Headers.Authorization = client.DefaultRequestHeaders.Authorization;
        request.Headers.TryAddWithoutValidation("Last-Event-ID", "9");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var events = await ReadChangeEventsAsync(reader, expectedMin: 3, TimeSpan.FromSeconds(5));
        events.Select(e => e.Id).ShouldBe(["10", "11", "12"]);
        events.Select(e => e.Id).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task Realtime_MembershipChange_ClosesStream()
    {
        _factory.Wms.OnGetChanges = () => ChangesBody();
        using var client = Authed(role: Permissions.Viewer);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/realtime");
        request.Headers.Authorization = client.DefaultRequestHeaders.Authorization;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Let subscription register
        await Task.Delay(100);
        var bus = _factory.Services.GetRequiredService<InMemoryTenantEventBus>();
        await bus.PublishAsync(_tenantId, new TenantBusMessage(
            $"lagerkraft.{_tenantId:D}.tenant.membership_changed",
            JsonSerializer.Serialize(new { user_id = _userId, session_version = 2 })));

        var closed = await WaitForStreamEndAsync(reader, TimeSpan.FromSeconds(3));
        closed.ShouldBeTrue();
    }

    [Fact]
    public void ChangePermissions_FloorWorker_FiltersTasksReadAll()
    {
        var worker = PrincipalWithRole(Permissions.FloorWorker);
        var viewer = PrincipalWithRole(Permissions.Viewer);
        ChangePermissions.CanSee(worker, "Task", Permissions.TasksReadOwn, null).ShouldBeTrue();
        ChangePermissions.CanSee(worker, "Task", Permissions.TasksReadAll, null).ShouldBeFalse();
        ChangePermissions.CanSee(viewer, "Task", Permissions.TasksReadAll, null).ShouldBeTrue();
    }

    private static ClaimsPrincipal PrincipalWithRole(string role)
    {
        var ra = JsonSerializer.Serialize(new[] { new Dictionary<string, object?> { ["r"] = role, ["w"] = "*" } });
        var id = new ClaimsIdentity("test");
        id.AddClaim(new System.Security.Claims.Claim(Lagerkraft.Shared.Auth.LagerkraftClaims.RoleAssignments, ra));
        return new ClaimsPrincipal(id);
    }

    [Fact]
    public async Task Realtime_ApplicationStopping_SendsRetry()
    {
        _factory.Wms.OnGetChanges = () => ChangesBody();
        // Host shutdown is hard through WAF; exercise ConnectionRegistry close + retry writer via stopping token
        // by cancelling after connect using a short-lived factory lifetime.
        using var client = Authed(role: Permissions.Viewer);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/realtime");
        request.Headers.Authorization = client.DefaultRequestHeaders.Authorization;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Dispose factory => application stopping
        var readTask = reader.ReadToEndAsync();
        await _factory.DisposeAsync();
        var body = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        body.ShouldContain("retry:");
    }

    private HttpClient Authed(string role)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // SSE needs unbuffered
        });
        var token = _factory.IssueToken(_userId, _tenantId, 1, _deviceId, role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static JsonElement ChangesBody(params JsonElement[] entries)
    {
        return JsonSerializer.SerializeToElement(new
        {
            feed_epoch = Guid.CreateVersion7(),
            entries
        });
    }

    private static JsonElement Entry(long seq, string entity, string? requiredPermission = null)
    {
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["entity"] = entity,
            ["id"] = Guid.CreateVersion7(),
            ["op"] = "upsert",
            ["payload"] = new { },
            ["recorded_at"] = DateTimeOffset.UtcNow,
            ["required_permission"] = requiredPermission
        });
    }

    private static TenantBusMessage Live(long seq, JsonElement entry) =>
        new(
            $"lagerkraft.00000000-0000-0000-0000-000000000001.inventory.task.created",
            entry.GetRawText(),
            seq,
            "Task",
            null,
            DateTimeOffset.UtcNow);

    private static async Task<List<SseEvent>> ReadChangeEventsAsync(StreamReader reader, int expectedMin, TimeSpan timeout)
    {
        var list = new List<SseEvent>();
        var deadline = DateTime.UtcNow + timeout;
        string? id = null;
        string? evt = null;
        var data = new StringBuilder();

        while (DateTime.UtcNow < deadline && list.Count < expectedMin)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                await Task.Delay(20);
                continue;
            }

            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                id = line[3..].Trim();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                evt = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].Trim());
            }
            else if (line.Length == 0 && evt == "change")
            {
                list.Add(new SseEvent(id ?? "", data.ToString()));
                id = null;
                evt = null;
                data.Clear();
            }
        }

        return list;
    }

    private static async Task<bool> WaitForStreamEndAsync(StreamReader reader, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record SseEvent(string Id, string Data);
}
