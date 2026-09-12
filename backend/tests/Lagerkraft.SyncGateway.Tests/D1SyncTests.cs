using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lagerkraft.SyncGateway.Clients;
using Lagerkraft.SyncGateway.Sync;

namespace Lagerkraft.SyncGateway.Tests;

[Trait("Category", "Integration")]
public sealed class D1SyncTests : IAsyncLifetime
{
    private readonly SyncGatewayApiFactory _factory = new();
    private Guid _tenantId;
    private Guid _userId;
    private Guid _deviceId;

    public Task InitializeAsync()
    {
        _tenantId = Guid.CreateVersion7();
        _userId = Guid.CreateVersion7();
        _deviceId = Guid.CreateVersion7();
        _factory.Platform.Devices[_deviceId] = new InternalDevice(
            _deviceId, _tenantId, "floor-1", [_tenantId], null, 0);
        _factory.Platform.Entitlement = new TenantEntitlement(_tenantId, "Trialing", false, "pro", 1000);
        _factory.Platform.CurrentSessionVersion = 1;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task Commands_SecondInFlightBatch_Returns409()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _factory.Wms.OnCommands = _ =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            var body = JsonSerializer.SerializeToElement(new { results = Array.Empty<object>() });
            return new WmsCommandResponse(HttpStatusCode.OK, body);
        };

        using var client = _factory.CreateClient();
        var token = _factory.IssueToken(_userId, _tenantId, 1, _deviceId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = Task.Run(() => client.PostAsJsonAsync("/sync/commands", SampleBatch()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await client.PostAsJsonAsync("/sync/commands", SampleBatch());
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        release.SetResult();
        (await first).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Commands_TrialExpired_Returns423_WithoutCallingWms()
    {
        _factory.Platform.Entitlement = new TenantEntitlement(_tenantId, "TrialExpired", false, "pro", 1000);
        using var client = Authed();
        var response = await client.PostAsJsonAsync("/sync/commands", SampleBatch());
        response.StatusCode.ShouldBe((HttpStatusCode)423);
        response.Headers.Contains(CompatHeaders.TenantState).ShouldBeTrue();
        _factory.Wms.CommandCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Commands_RevokedDevice_Returns410()
    {
        _factory.Platform.Devices[_deviceId] = new InternalDevice(
            _deviceId, _tenantId, "floor-1", [], DateTimeOffset.UtcNow, 0);
        using var client = Authed();
        var response = await client.PostAsJsonAsync("/sync/commands", SampleBatch());
        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        _factory.Wms.CommandCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Commands_StaleSv_FlushAccepted()
    {
        _factory.Platform.CurrentSessionVersion = 5;
        using var client = Authed(sessionVersion: 1);
        var response = await client.PostAsJsonAsync("/sync/commands", SampleBatch());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _factory.Wms.CommandCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Changes_StaleSv_Returns401()
    {
        _factory.Platform.CurrentSessionVersion = 5;
        using var client = Authed(sessionVersion: 1);
        var response = await client.GetAsync("/sync/changes?since=0");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SyncResponses_IncludeCompatHeaders()
    {
        using var client = Authed();
        var response = await client.GetAsync("/sync/compat");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues(CompatHeaders.MinAppVersion).Single().ShouldBe("0.9.0");
        response.Headers.GetValues(CompatHeaders.LatestAppVersion).Single().ShouldBe("1.0.0");
    }

    [Fact]
    public async Task Beacon_UpdatesPlatformDevice()
    {
        using var client = Authed();
        var response = await client.PostAsJsonAsync("/sync/beacon", new
        {
            pending_count = 3,
            oldest_pending_occurred_at = (DateTimeOffset?)null,
            last_sync_at = DateTimeOffset.UtcNow,
            sse_connected = true
        });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _factory.Platform.Beacons.Count.ShouldBe(1);
        _factory.Platform.Beacons.ToArray()[0].Beacon.PendingCount.ShouldBe(3);
        _factory.Platform.Devices[_deviceId].PendingCount.ShouldBe(3);
    }

    [Fact]
    public async Task Changes_SetsFeedEpochHeader()
    {
        using var client = Authed();
        var response = await client.GetAsync("/sync/changes?since=0");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues(CompatHeaders.FeedEpoch).Single()
            .ShouldBe(_factory.Wms.FeedEpoch.ToString());
    }

    private HttpClient Authed(int sessionVersion = 1)
    {
        var client = _factory.CreateClient();
        var token = _factory.IssueToken(_userId, _tenantId, sessionVersion, _deviceId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private object SampleBatch() => new
    {
        now = DateTimeOffset.UtcNow,
        commands = new[]
        {
            new
            {
                id = Guid.CreateVersion7(),
                type = "Probe",
                v = 1,
                payload = JsonSerializer.SerializeToElement(new { probe_id = Guid.CreateVersion7(), message = "x" }),
                occurred_at = DateTimeOffset.UtcNow,
                device_id = _deviceId,
                user_id = _userId
            }
        }
    };
}
