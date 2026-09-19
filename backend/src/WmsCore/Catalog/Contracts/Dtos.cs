namespace Lagerkraft.WmsCore.Catalog.Contracts;

public static class CatalogDefaults
{
    public static readonly Guid StId = Guid.Parse("01900000-0000-7000-8000-000000000101");
    public static readonly Guid KgId = Guid.Parse("01900000-0000-7000-8000-000000000102");
    public static readonly Guid GId = Guid.Parse("01900000-0000-7000-8000-000000000103");
    public static readonly Guid LId = Guid.Parse("01900000-0000-7000-8000-000000000104");
    public static readonly Guid MlId = Guid.Parse("01900000-0000-7000-8000-000000000105");
    public static readonly Guid MId = Guid.Parse("01900000-0000-7000-8000-000000000106");
    public static readonly Guid CmId = Guid.Parse("01900000-0000-7000-8000-000000000107");
    public static readonly Guid M2Id = Guid.Parse("01900000-0000-7000-8000-000000000108");
    public static readonly Guid M3Id = Guid.Parse("01900000-0000-7000-8000-000000000109");

    public static readonly UnitOfMeasureDto[] Units =
    [
        new(StId, "st", "count", "1", "st", "st"),
        new(KgId, "kg", "mass", "1", "kg", "kg"),
        new(GId, "g", "mass", "0.001", "g", "g"),
        new(LId, "l", "volume", "1", "l", "l"),
        new(MlId, "ml", "volume", "0.001", "ml", "ml"),
        new(MId, "m", "length", "1", "m", "m"),
        new(CmId, "cm", "length", "0.01", "cm", "cm"),
        new(M2Id, "m2", "area", "1", "m²", "m²"),
        new(M3Id, "m3", "volume", "1000", "m³", "m³")
    ];
}

public sealed record UnitOfMeasureDto(
    Guid Id,
    string Code,
    string Dimension,
    string FactorToDimensionBase,
    string DisplayNameSv,
    string DisplayNameEn);

public sealed record PackagingLevelDto(Guid Id, Guid ArticleId, int Rank, string Name, string QtyInBase);

public sealed record ArticleDto(
    Guid Id,
    string Sku,
    string Name,
    string Status,
    Guid BaseUomId,
    int QuantityPrecision,
    string QuantityStep,
    bool AllowLoosePick,
    PackagingLevelDto[] PackagingLevels);

public sealed record CreateArticleRequest(
    Guid Id,
    string Sku,
    string Name,
    Guid? BaseUomId,
    Guid? PackagingLevelId);
