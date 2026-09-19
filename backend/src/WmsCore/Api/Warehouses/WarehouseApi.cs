using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Internal;
using Lagerkraft.WmsCore.Api.Tenancy;
using Lagerkraft.WmsCore.Layout.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Warehouses;

public static class WarehouseApi
{
    public static WebApplication MapWarehouseApi(this WebApplication app)
    {
        app.MapPost("/warehouses", Create);
        app.MapGet("/warehouses", List);
        app.MapPost("/warehouses/{id:guid}/activate", Activate);
        var group = app.MapGroup("/internal").AddEndpointFilter<InternalTokenFilter>();
        group.MapGet("/warehouses/{id:guid}", GetInternal);
        return app;
    }

    private static async Task<IResult> Create(
        CreateWarehouseRequest body,
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

        var id = body.Id is { } given && given != Guid.Empty ? given : Ids.New();
        var pattern = string.IsNullOrWhiteSpace(body.CodePattern)
            ? LayoutDefaults.CodePattern
            : body.CodePattern;
        db.Warehouses.Add(new Warehouse
        {
            Id = id,
            Name = body.Name,
            CodePattern = pattern,
            ClaimMinutes = body.ClaimMinutes ?? 30,
            NightShift = body.NightShift,
            BlindCount = body.BlindCount,
            ZonePicking = body.ZonePicking
        });
        foreach (var code in LayoutDefaults.SystemLocationCodes)
        {
            db.Locations.Add(new Location
            {
                Id = Ids.New(),
                WarehouseId = id,
                Type = "staging",
                Code = code,
                Path = code,
                Barcode = code,
                Status = "active",
                IsSystem = true
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.Created($"/warehouses/{id}", ToDto(await db.Warehouses.AsNoTracking().SingleAsync(w => w.Id == id, ct)));
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

        var rows = await db.Warehouses.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
        return Results.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> Activate(
        Guid id,
        ITenantConnectionCache connections,
        IClock clock,
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

        var warehouse = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (warehouse is null)
        {
            return Results.NotFound();
        }

        warehouse.ActivatedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(warehouse));
    }

    private static async Task<IResult> GetInternal(
        Guid id,
        Guid tenantId,
        ITenantConnectionCache connections,
        CancellationToken ct)
    {
        await using var db = await OpenAsync(connections, tenantId, ct);
        if (db is null)
        {
            return Results.NotFound();
        }

        var wh = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        return wh is null ? Results.NotFound() : Results.Ok(wh);
    }

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

    private static WarehouseListDto ToDto(Warehouse w) =>
        new(w.Id, w.Name, w.CodePattern, w.ActivatedAt);
}

public sealed record CreateWarehouseRequest(
    Guid? Id,
    string Name,
    string? CodePattern,
    int? ClaimMinutes,
    bool NightShift,
    bool BlindCount,
    bool ZonePicking);
