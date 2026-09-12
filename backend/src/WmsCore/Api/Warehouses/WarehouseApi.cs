using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Internal;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Warehouses;

public static class WarehouseApi
{
    public static WebApplication MapWarehouseApi(this WebApplication app)
    {
        app.MapPost("/warehouses", Create);
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
        if (!http.Request.Headers.TryGetValue("Lagerkraft-Tenant-Id", out var tenantHeader)
            || !Guid.TryParse(tenantHeader, out var tenantId))
        {
            return Results.BadRequest("Lagerkraft-Tenant-Id required");
        }

        var cs = await connections.GetConnectionStringAsync(tenantId, ct);
        if (cs is null)
        {
            return Results.NotFound();
        }

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new TenantDbContext(options);
        var id = body.Id is { } given && given != Guid.Empty ? given : Ids.New();
        db.Warehouses.Add(new Warehouse
        {
            Id = id,
            Name = body.Name,
            CodePattern = body.CodePattern,
            ClaimMinutes = body.ClaimMinutes ?? 30,
            NightShift = body.NightShift,
            BlindCount = body.BlindCount,
            ZonePicking = body.ZonePicking
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/warehouses/{id}", new { id, body.Name });
    }

    private static async Task<IResult> GetInternal(
        Guid id,
        Guid tenantId,
        ITenantConnectionCache connections,
        CancellationToken ct)
    {
        var cs = await connections.GetConnectionStringAsync(tenantId, ct);
        if (cs is null)
        {
            return Results.NotFound();
        }

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new TenantDbContext(options);
        var wh = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        return wh is null ? Results.NotFound() : Results.Ok(wh);
    }
}

public sealed record CreateWarehouseRequest(
    Guid? Id,
    string Name,
    string? CodePattern,
    int? ClaimMinutes,
    bool NightShift,
    bool BlindCount,
    bool ZonePicking);
