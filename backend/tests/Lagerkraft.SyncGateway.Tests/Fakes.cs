using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Lagerkraft.SyncGateway.Clients;

namespace Lagerkraft.SyncGateway.Tests;

public sealed class FakePlatformSyncClient : IPlatformSyncClient
{
    public ConcurrentDictionary<Guid, InternalDevice> Devices { get; } = new();
    public TenantEntitlement? Entitlement { get; set; }
    public int CurrentSessionVersion { get; set; } = 1;
    public ConcurrentBag<(Guid DeviceId, DeviceBeaconPayload Beacon)> Beacons { get; } = new();

    public Task<InternalDevice?> GetDeviceAsync(Guid deviceId, CancellationToken ct) =>
        Task.FromResult(Devices.TryGetValue(deviceId, out var d) ? d : null);

    public Task<TenantEntitlement?> GetEntitlementAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(Entitlement);

    public Task<int?> GetSessionVersionAsync(Guid tenantId, Guid userId, CancellationToken ct) =>
        Task.FromResult<int?>(CurrentSessionVersion);

    public Task PostBeaconAsync(Guid deviceId, DeviceBeaconPayload beacon, CancellationToken ct)
    {
        Beacons.Add((deviceId, beacon));
        if (Devices.TryGetValue(deviceId, out var d))
        {
            Devices[deviceId] = d with { PendingCount = beacon.PendingCount };
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeWmsCoreSyncClient : IWmsCoreSyncClient
{
    public int CommandCalls { get; private set; }
    public Func<CommandBatchPayload, WmsCommandResponse>? OnCommands { get; set; }
    public Guid FeedEpoch { get; set; } = Guid.CreateVersion7();
    public Func<JsonElement>? OnGetChanges { get; set; }

    public Task<WmsCommandResponse> PostCommandsAsync(CommandBatchPayload batch, CancellationToken ct)
    {
        CommandCalls++;
        if (OnCommands is not null)
        {
            return Task.FromResult(OnCommands(batch));
        }

        var body = JsonSerializer.SerializeToElement(new
        {
            results = batch.Commands.Select(c => new { command_id = c.Id, outcome = "Applied" }).ToArray()
        });
        return Task.FromResult(new WmsCommandResponse(HttpStatusCode.OK, body));
    }

    public Task<WmsChangesResponse> GetChangesAsync(Guid tenantId, Guid? warehouse, long? since, CancellationToken ct)
    {
        if (OnGetChanges is not null)
        {
            var custom = OnGetChanges();
            Guid? epoch = FeedEpoch;
            if (custom.ValueKind == JsonValueKind.Object
                && custom.TryGetProperty("feed_epoch", out var ep)
                && ep.ValueKind == JsonValueKind.String
                && Guid.TryParse(ep.GetString(), out var g))
            {
                epoch = g;
            }

            return Task.FromResult(new WmsChangesResponse(HttpStatusCode.OK, epoch, custom));
        }

        var body = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = FeedEpoch,
            warehouse,
            entries = Array.Empty<object>()
        });
        return Task.FromResult(new WmsChangesResponse(HttpStatusCode.OK, FeedEpoch, body));
    }

    public Task<JsonElement> GetSnapshotAsync(Guid tenantId, Guid? warehouse, string? entity, int? page, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToElement(new
        {
            feed_epoch = FeedEpoch,
            warehouse,
            entity = entity ?? "Task",
            page = page ?? 1,
            snapshot_schema = "InitialTenant",
            items = Array.Empty<object>()
        });
        return Task.FromResult(body);
    }

    public Task<CompatInfo> GetCompatAsync(CancellationToken ct) =>
        Task.FromResult(new CompatInfo(
            new Dictionary<string, int> { ["Probe"] = 1 },
            "1.0.0",
            "0.9.0"));
}
