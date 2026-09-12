using System.Text.Json.Serialization;

namespace Lagerkraft.Platform.Devices;

public sealed record CreateEnrollmentCodeRequest(
    [property: JsonPropertyName("warehouse_ids")] Guid[]? WarehouseIds);

public sealed record EnrollmentCodeResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record EnrollDeviceRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("device_id")] Guid? DeviceId);

public sealed record EnrollDeviceResponse(
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("device_secret")] string DeviceSecret,
    [property: JsonPropertyName("warehouse_ids")] Guid[] WarehouseIds,
    [property: JsonPropertyName("tenant_id")] Guid TenantId);

public sealed record DeviceListItem(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("warehouse_ids")] Guid[] WarehouseIds,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("last_sync_at")] DateTimeOffset? LastSyncAt,
    [property: JsonPropertyName("pending_count")] int PendingCount,
    [property: JsonPropertyName("oldest_pending_occurred_at")] DateTimeOffset? OldestPendingOccurredAt);

public sealed record RemoveDeviceRequest(
    [property: JsonPropertyName("confirm_loss")] bool ConfirmLoss);

public sealed record DeviceBeaconRequest(
    [property: JsonPropertyName("pending_count")] int PendingCount,
    [property: JsonPropertyName("oldest_pending_occurred_at")] DateTimeOffset? OldestPendingOccurredAt,
    [property: JsonPropertyName("last_sync_at")] DateTimeOffset? LastSyncAt,
    [property: JsonPropertyName("sse_connected")] bool? SseConnected);

public sealed record InternalDeviceResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("warehouse_ids")] Guid[] WarehouseIds,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("pending_count")] int PendingCount);
