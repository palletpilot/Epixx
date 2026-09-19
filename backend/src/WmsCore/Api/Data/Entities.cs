using Lagerkraft.Shared;

namespace Lagerkraft.WmsCore.Api.Data;

public sealed class TenantMeta
{
    public int Id { get; set; } = 1;
    public Guid FeedEpoch { get; set; } = Ids.New();
    public string SchemaVersion { get; set; } = "";
}

public sealed class ChangeLogRow
{
    public long Seq { get; set; }
    public string Entity { get; set; } = "";
    public Guid Id { get; set; }
    public string Op { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public Guid? CommandId { get; set; }
    public Guid? Actor { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class OutboxRow
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class ProcessedCommand
{
    public Guid CommandId { get; set; }
    public string Result { get; set; } = "{}";
    public DateTimeOffset AppliedAt { get; set; }
}

public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public sealed class Deviation
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public Guid? CommandId { get; set; }
    public string Detail { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Warehouse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? CodePattern { get; set; }
    public string? OperatingHours { get; set; }
    public bool NightShift { get; set; }
    public int ClaimMinutes { get; set; } = 30;
    public bool BlindCount { get; set; }
    public decimal? CountAutoAdjustThreshold { get; set; }
    public bool PackStep { get; set; }
    public bool ZonePicking { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public int? DefaultHeightMm { get; set; }
    public int? DefaultWidthMm { get; set; }
    public int? DefaultDepthMm { get; set; }
    public int? DefaultMaxWeightG { get; set; }
}

public sealed class Location
{
    public Guid Id { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid? ParentId { get; set; }
    public string Type { get; set; } = "bin";
    public string Code { get; set; } = "";
    public string Path { get; set; } = "";
    public int? HeightMm { get; set; }
    public int? WidthMm { get; set; }
    public int? DepthMm { get; set; }
    public int? MaxWeightG { get; set; }
    public string Barcode { get; set; } = "";
    public string Status { get; set; } = "active";
    public bool IsSystem { get; set; }
}

public sealed class WarehouseTask
{
    public Guid Id { get; set; }
    public Guid WarehouseId { get; set; }
    public string Type { get; set; } = "putaway";
    public string Status { get; set; } = "open";
    public Guid? AssigneeUserId { get; set; }
    public DateTimeOffset? AssignedUntil { get; set; }
    public Guid? SuggestedLocationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TaskLine
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid? ArticleId { get; set; }
    public decimal RequestedQtyBase { get; set; }
    public decimal PickedQtyBase { get; set; }
    public Guid? FromLocationId { get; set; }
    public Guid? FromHandlingUnitId { get; set; }
    public string? SuggestedBreakdown { get; set; }
    public decimal? TolerancePct { get; set; }
    public string Status { get; set; } = "open";
}

public sealed class UnitOfMeasure
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Dimension { get; set; } = "";
    public decimal FactorToDimensionBase { get; set; }
    public string DisplayNameSv { get; set; } = "";
    public string DisplayNameEn { get; set; } = "";
}

public sealed class Article
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Gtin { get; set; }
    public string Status { get; set; } = "";
    public Guid BaseUomId { get; set; }
    public int QuantityPrecision { get; set; }
    public decimal QuantityStep { get; set; }
    public bool AllowLoosePick { get; set; }
    public Guid? OrderMultipleLevelId { get; set; }
    public int? WeightPerBaseUnitG { get; set; }
    public string? PickInstruction { get; set; }
    public bool CatchWeight { get; set; }
    public Guid? CatchWeightUomId { get; set; }
}

public sealed class PackagingLevel
{
    public Guid Id { get; set; }
    public Guid ArticleId { get; set; }
    public int Rank { get; set; }
    public string Name { get; set; } = "";
    public decimal QtyInBase { get; set; }
    public int HeightMm { get; set; }
    public int WidthMm { get; set; }
    public int DepthMm { get; set; }
    public int GrossWeightG { get; set; }
    public string? Barcode { get; set; }
    public Guid? PalletTypeId { get; set; }
    public bool IsBreakable { get; set; }
    public bool IsDefaultReceiving { get; set; }
    public bool IsDefaultShipping { get; set; }
}
