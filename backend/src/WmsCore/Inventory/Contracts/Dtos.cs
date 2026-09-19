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

public sealed record HandlingUnitContentDto(
    Guid Id,
    Guid HandlingUnitId,
    Guid ArticleId,
    string QtyBase,
    Guid PackagingLevelId);

public sealed record HandlingUnitDto(
    Guid Id,
    Guid WarehouseId,
    string Lpn,
    int? HeightMm,
    DateTimeOffset ReceivedAt,
    HandlingUnitContentDto[] Contents);

public sealed record StockBalanceDto(
    Guid LocationId,
    Guid ArticleId,
    Guid HandlingUnitId,
    string QtyBase,
    string ReservedQtyBase);
