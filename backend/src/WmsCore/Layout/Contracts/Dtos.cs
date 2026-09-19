namespace Lagerkraft.WmsCore.Layout.Contracts;

public static class LayoutDefaults
{
    public const string CodePattern = "{aisle}-{rack:2}-{level:2}-{bin:2}";

    public static readonly string[] SystemLocationCodes =
        ["RECEIVING", "PICKING", "SHIPPING", "QUARANTINE", "FLOOR", "ADJUSTMENT"];
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
