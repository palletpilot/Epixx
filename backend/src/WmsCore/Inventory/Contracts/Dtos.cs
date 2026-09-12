namespace Lagerkraft.WmsCore.Inventory.Contracts;

public sealed record TaskDto(
    Guid Id,
    Guid WarehouseId,
    string Type,
    string Status,
    Guid? AssigneeUserId,
    DateTimeOffset? AssignedUntil,
    Guid? SuggestedLocationId,
    DateTimeOffset CreatedAt);

public sealed record WarehouseDto(
    Guid Id,
    string Name,
    string? CodePattern,
    int ClaimMinutes,
    bool NightShift,
    bool BlindCount,
    bool ZonePicking);
