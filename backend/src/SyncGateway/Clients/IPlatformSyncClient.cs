namespace Lagerkraft.SyncGateway.Clients;

public interface IPlatformSyncClient
{
    Task<InternalDevice?> GetDeviceAsync(Guid deviceId, CancellationToken ct);
    Task<TenantEntitlement?> GetEntitlementAsync(Guid tenantId, CancellationToken ct);
    Task<int?> GetSessionVersionAsync(Guid tenantId, Guid userId, CancellationToken ct);
    Task PostBeaconAsync(Guid deviceId, DeviceBeaconPayload beacon, CancellationToken ct);
}

public sealed record InternalDevice(
    Guid Id,
    Guid TenantId,
    string Name,
    Guid[] WarehouseIds,
    DateTimeOffset? RevokedAt,
    int PendingCount);

public sealed record TenantEntitlement(
    Guid TenantId,
    string LifecycleState,
    bool Maintenance,
    string PlanCode,
    int? HardCapUnits);

public sealed record DeviceBeaconPayload(
    int PendingCount,
    DateTimeOffset? OldestPendingOccurredAt,
    DateTimeOffset? LastSyncAt,
    bool? SseConnected);
