using System.Globalization;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Lagerkraft.WmsCore.Outbound.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Outbound;

public static class OrderApi
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static WebApplication MapOrderApi(this WebApplication app)
    {
        app.MapPost("/orders", Create);
        app.MapGet("/orders", List);
        return app;
    }

    private static async Task<IResult> Create(
        CreateOrderRequest body,
        ITenantConnectionCache connections,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        if (!TryTenant(http, out var tenantId))
        {
            return Results.BadRequest("Lagerkraft-Tenant-Id required");
        }

        if (body.Id == Guid.Empty || body.WarehouseId == Guid.Empty || body.ArticleId == Guid.Empty)
        {
            return Results.BadRequest();
        }

        if (!decimal.TryParse(body.QtyBase, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty)
            || qty <= 0)
        {
            return Results.BadRequest(new { error = "invalid_qty" });
        }

        await using var db = await OpenAsync(connections, tenantId, ct);
        if (db is null)
        {
            return Results.NotFound();
        }

        var warehouse = await db.Warehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == body.WarehouseId, ct);
        if (warehouse is null)
        {
            return Results.BadRequest(new { error = "unknown_warehouse" });
        }

        var article = await db.Articles.FirstOrDefaultAsync(a => a.Id == body.ArticleId, ct);
        if (article is null)
        {
            return Results.BadRequest(new { error = "unknown_article" });
        }

        var level = body.RequestedLevelId is { } levelId && levelId != Guid.Empty
            ? await db.PackagingLevels.FirstOrDefaultAsync(
                l => l.Id == levelId && l.ArticleId == article.Id, ct)
            : await db.PackagingLevels
                .Where(l => l.ArticleId == article.Id)
                .OrderBy(l => l.Rank)
                .FirstOrDefaultAsync(ct);
        if (level is null)
        {
            return Results.BadRequest(new { error = "unknown_packaging_level" });
        }

        var externalRef = string.IsNullOrWhiteSpace(body.ExternalRef)
            ? $"ORD-{body.Id.ToString("N")[..8].ToUpperInvariant()}"
            : body.ExternalRef.Trim();
        if (await db.OutboundOrders.AnyAsync(
                o => o.WarehouseId == warehouse.Id && o.Source == "manual" && o.ExternalRef == externalRef,
                ct))
        {
            return Results.Conflict(new { error = "duplicate_external_ref" });
        }

        var now = clock.UtcNow;
        var lineId = Ids.New();
        var order = new OutboundOrder
        {
            Id = body.Id,
            WarehouseId = warehouse.Id,
            Source = "manual",
            ExternalRef = externalRef,
            DestinationName = body.DestinationName?.Trim() ?? "",
            Status = "released"
        };
        var line = new OutboundOrderLine
        {
            Id = lineId,
            OrderId = order.Id,
            ArticleId = article.Id,
            RequestedQtyBase = qty,
            RequestedLevelId = level.Id,
            AllocatedQtyBase = 0,
            TolerancePct = 0,
            Status = "open"
        };
        db.OutboundOrders.Add(order);
        db.OutboundOrderLines.Add(line);

        var pick = await (
            from b in db.StockBalances
            join loc in db.Locations on b.LocationId equals loc.Id
            join hu in db.HandlingUnits on b.HandlingUnitId equals hu.Id
            where loc.WarehouseId == warehouse.Id
                && !loc.IsSystem
                && loc.Code != "QUARANTINE"
                && (loc.Status == null || loc.Status == "active")
                && b.ArticleId == article.Id
                && article.Status == "published"
                && b.QtyBase - b.ReservedQtyBase >= qty
            orderby hu.ReceivedAt, loc.Code
            select new { b.LocationId, b.HandlingUnitId, HuReceivedAt = hu.ReceivedAt }
        ).FirstOrDefaultAsync(ct);

        WarehouseTask? task = null;
        if (pick is null)
        {
            line.Status = "short";
        }
        else
        {
            var balance = await db.StockBalances.FindAsync(
                [pick.LocationId, article.Id, pick.HandlingUnitId], ct);
            if (balance is null)
            {
                line.Status = "short";
            }
            else
            {
                balance.ReservedQtyBase += qty;
                line.AllocatedQtyBase = qty;
                order.Status = "allocated";
                task = new WarehouseTask
                {
                    Id = Ids.New(),
                    WarehouseId = warehouse.Id,
                    Type = "pick",
                    Status = "open",
                    SuggestedLocationId = pick.LocationId,
                    CreatedAt = now
                };
                db.Tasks.Add(task);
                db.TaskLines.Add(new TaskLine
                {
                    Id = Ids.New(),
                    TaskId = task.Id,
                    ArticleId = article.Id,
                    RequestedQtyBase = qty,
                    FromLocationId = pick.LocationId,
                    FromHandlingUnitId = pick.HandlingUnitId,
                    SuggestedBreakdown = JsonSerializer.Serialize(new { order_id = order.Id }, Json),
                    Status = "open"
                });
            }
        }

        var dto = ToDto(order, [line]);
        db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = "order",
            Id = order.Id,
            Op = "upsert",
            Payload = JsonSerializer.Serialize(dto, Json),
            OccurredAt = now,
            RecordedAt = now
        });
        if (task is not null)
        {
            db.ChangeLog.Add(new ChangeLogRow
            {
                Entity = "task",
                Id = task.Id,
                Op = "upsert",
                Payload = JsonSerializer.Serialize(new
                {
                    id = task.Id,
                    warehouse_id = task.WarehouseId,
                    type = task.Type,
                    status = task.Status,
                    assignee_user_id = task.AssigneeUserId,
                    assigned_until = task.AssignedUntil,
                    suggested_location_id = task.SuggestedLocationId,
                    created_at = task.CreatedAt
                }, Json),
                OccurredAt = now,
                RecordedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        return pick is null
            ? Results.Ok(dto)
            : Results.Created($"/orders/{order.Id}", dto);
    }

    private static async Task<IResult> List(
        Guid? warehouse,
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

        var q = db.OutboundOrders.AsNoTracking().AsQueryable();
        if (warehouse is { } wh)
        {
            q = q.Where(o => o.WarehouseId == wh);
        }

        var orders = await q.OrderByDescending(o => o.Id).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var lines = ids.Count == 0
            ? []
            : await db.OutboundOrderLines.AsNoTracking()
                .Where(l => ids.Contains(l.OrderId))
                .ToListAsync(ct);
        var byOrder = lines.ToLookup(l => l.OrderId);
        return Results.Ok(orders.Select(o => ToDto(o, byOrder[o.Id])).ToList());
    }

    private static OrderDto ToDto(OutboundOrder order, IEnumerable<OutboundOrderLine> lines) =>
        new(
            order.Id,
            order.WarehouseId,
            order.Source,
            order.ExternalRef,
            order.DestinationName,
            order.RequestedShipDate,
            order.Status,
            order.Notes,
            lines.Select(l => new OrderLineDto(
                l.Id,
                l.OrderId,
                l.ArticleId,
                DecimalString(l.RequestedQtyBase),
                l.RequestedLevelId,
                DecimalString(l.AllocatedQtyBase),
                DecimalString(l.TolerancePct),
                l.Status)).ToArray());

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
