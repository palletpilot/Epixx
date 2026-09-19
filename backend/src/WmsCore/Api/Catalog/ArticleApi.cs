using System.Globalization;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Lagerkraft.WmsCore.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Catalog;

public static class ArticleApi
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static WebApplication MapArticleApi(this WebApplication app)
    {
        app.MapPost("/articles", Create);
        app.MapGet("/articles", List);
        app.MapGet("/units", ListUnits);
        return app;
    }

    private static async Task<IResult> Create(
        CreateArticleRequest body,
        ITenantConnectionCache connections,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        if (!TryTenant(http, out var tenantId))
        {
            return Results.BadRequest("Lagerkraft-Tenant-Id required");
        }

        if (body.Id == Guid.Empty)
        {
            return Results.BadRequest();
        }

        var sku = body.Sku?.Trim() ?? "";
        if (sku.Length is < 1 or > 64)
        {
            return Results.BadRequest();
        }

        var name = body.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200)
        {
            return Results.BadRequest();
        }

        await using var db = await OpenAsync(connections, tenantId, ct);
        if (db is null)
        {
            return Results.NotFound();
        }

        var uomId = body.BaseUomId ?? CatalogDefaults.StId;
        var uom = await db.UnitsOfMeasure.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uomId, ct);
        if (uom is null)
        {
            return Results.BadRequest(new { error = "unknown_uom" });
        }

        if (await db.Articles.AnyAsync(a => a.Sku == sku, ct))
        {
            return Results.Conflict(new { error = "duplicate_sku" });
        }

        var count = string.Equals(uom.Dimension, "count", StringComparison.Ordinal);
        var article = new Article
        {
            Id = body.Id,
            Sku = sku,
            Name = name,
            Status = "published",
            BaseUomId = uom.Id,
            QuantityPrecision = count ? 0 : 3,
            QuantityStep = count ? 1m : 0.001m,
            AllowLoosePick = true,
            CatchWeight = false
        };
        var level = new PackagingLevel
        {
            Id = body.PackagingLevelId is { } given && given != Guid.Empty ? given : Ids.New(),
            ArticleId = article.Id,
            Rank = 1,
            Name = uom.DisplayNameSv,
            QtyInBase = 1m,
            IsBreakable = true,
            IsDefaultReceiving = true,
            IsDefaultShipping = true
        };
        db.Articles.Add(article);
        db.PackagingLevels.Add(level);
        var dto = ToArticleDto(article, [level]);
        var now = clock.UtcNow;
        db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = "article",
            Id = article.Id,
            Op = "upsert",
            Payload = JsonSerializer.Serialize(dto, Json),
            OccurredAt = now,
            RecordedAt = now
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/articles/{article.Id}", dto);
    }

    private static async Task<IResult> List(
        ITenantConnectionCache connections,
        HttpContext http,
        CancellationToken ct)
    {
        if (!TryTenant(http, out var tenantId))
        {
            return Results.BadRequest("Lagerkraft-Tenant-Id required");
        }

        await using var db = await OpenAsync(connections, tenantId, ct);
        if (db is null)
        {
            return Results.NotFound();
        }

        var articles = await db.Articles.AsNoTracking().OrderBy(a => a.Sku).ToListAsync(ct);
        return Results.Ok(await WithLevelsAsync(db, articles, ct));
    }

    private static async Task<IResult> ListUnits(
        ITenantConnectionCache connections,
        HttpContext http,
        CancellationToken ct)
    {
        if (!TryTenant(http, out var tenantId))
        {
            return Results.BadRequest("Lagerkraft-Tenant-Id required");
        }

        await using var db = await OpenAsync(connections, tenantId, ct);
        if (db is null)
        {
            return Results.NotFound();
        }

        var units = await db.UnitsOfMeasure.AsNoTracking().OrderBy(u => u.Code).ToListAsync(ct);
        return Results.Ok(units.Select(ToUnitDto).ToList());
    }

    internal static async Task<List<ArticleDto>> WithLevelsAsync(
        TenantDbContext db,
        List<Article> articles,
        CancellationToken ct)
    {
        var ids = articles.Select(a => a.Id).ToList();
        var levels = ids.Count == 0
            ? []
            : await db.PackagingLevels.AsNoTracking()
                .Where(l => ids.Contains(l.ArticleId))
                .OrderBy(l => l.Rank)
                .ToListAsync(ct);
        var byArticle = levels.ToLookup(l => l.ArticleId);
        return articles.Select(a => ToArticleDto(a, byArticle[a.Id])).ToList();
    }

    internal static ArticleDto ToArticleDto(Article article, IEnumerable<PackagingLevel> levels) =>
        new(
            article.Id,
            article.Sku,
            article.Name,
            article.Status,
            article.BaseUomId,
            article.QuantityPrecision,
            DecimalString(article.QuantityStep),
            article.AllowLoosePick,
            levels.Select(ToLevelDto).ToArray());

    internal static UnitOfMeasureDto ToUnitDto(UnitOfMeasure unit) =>
        new(
            unit.Id,
            unit.Code,
            unit.Dimension,
            DecimalString(unit.FactorToDimensionBase),
            unit.DisplayNameSv,
            unit.DisplayNameEn);

    private static PackagingLevelDto ToLevelDto(PackagingLevel level) =>
        new(level.Id, level.ArticleId, level.Rank, level.Name, DecimalString(level.QtyInBase));

    private static string DecimalString(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static bool TryTenant(HttpContext http, out Guid tenantId)
    {
        tenantId = default;
        return http.Request.Headers.TryGetValue("Lagerkraft-Tenant-Id", out var tenantHeader)
            && Guid.TryParse(tenantHeader, out tenantId);
    }

    private static async Task<TenantDbContext?> OpenAsync(
        ITenantConnectionCache connections,
        Guid tenantId,
        CancellationToken ct)
    {
        var cs = await connections.GetConnectionStringAsync(tenantId, ct);
        if (cs is null)
        {
            return null;
        }

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new TenantDbContext(options);
    }
}
