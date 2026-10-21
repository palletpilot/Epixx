namespace Lagerkraft.WmsCore.Layout.Contracts;

public static class LayoutDefaults
{
    public const string CodePattern = "{aisle}-{rack:2}-{level:2}-{bin:2}";

    public const string Receiving = "RECEIVING";
    public const string Picking = "PICKING";
    public const string Shipping = "SHIPPING";
    public const string Quarantine = "QUARANTINE";
    public const string Floor = "FLOOR";
    public const string Adjustment = "ADJUSTMENT";
    public const string Unlocated = "UNLOCATED";

    public static readonly string[] SystemLocationCodes =
        [Receiving, Picking, Shipping, Quarantine, Floor, Adjustment, Unlocated];

    public static bool IsForbiddenPutawayCode(string code) =>
        code is Unlocated or Adjustment;
}

public sealed record LocationDto(
    Guid Id,
    Guid WarehouseId,
    Guid? ParentId,
    string Type,
    string Code,
    string Path,
    int? HeightMm,
    int? WidthMm,
    int? DepthMm,
    int? MaxWeightG,
    string Barcode,
    string Status,
    bool IsSystem);

public sealed record WarehouseListDto(
    Guid Id,
    string Name,
    string? CodePattern,
    DateTimeOffset? ActivatedAt);
