using System.Globalization;
using System.Text.Json;
using Lagerkraft.WmsCore.Api.Catalog;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Layout.Contracts;
using Lagerkraft.WmsCore.Inventory.Contracts;
using Lagerkraft.WmsCore.Outbound.Contracts;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Migrations;
using Lagerkraft.WmsCore.Api.Relay;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Internal;

public static class InternalApi
{
    public static WebApplication MapInternalApi(this WebApplication app)
    {
        var group = app.MapGroup("/internal").AddEndpointFilter<InternalTokenFilter>();
        group.MapPost("/tenants/{id:guid}/migrate", Migrate);
        group.MapPost("/commands", PostCommands);
        group.MapGet("/changes", GetChanges);
        group.MapGet("/snapshot", GetSnapshot);
        group.MapGet("/compat", GetCompat);
        return app;
    }

    private static async Task<IResult> Migrate(
        Guid id,
        ITenantMigrator migrator,
        OutboxRelay relay,
        OutboxRetentionJob retention,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("Lagerkraft.WmsCore.Api.Internal.Migrate");
        try
        {
            await migrator.MigrateTenantAsync(id, ct);
            relay.TrackTenant(id);
            retention.TrackTenant(id);
            return Results.Ok();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("connection not found", StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Migrate failed for tenant {TenantId}", id);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> PostCommands(
        CommandBatchRequest body,
        CommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var batch = new CommandBatch(
            body.TenantId,
            body.Now,
            body.Commands.Select(c => new CommandEnvelope(
                c.Id,
                c.Type,
                c.V,
                c.Payload,
                c.OccurredAt,
                c.DeviceId,
                c.UserId)).ToList());

        var result = await dispatcher.DispatchAsync(batch, ct);
        if (result.UpgradeRequired)
        {
            return Results.Json(result, statusCode: StatusCodes.Status426UpgradeRequired);
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> GetChanges(
        Guid tenantId,
        Guid? warehouse,
        long? since,
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
        var meta = await db.TenantMeta.SingleOrDefaultAsync(ct);
        var feedEpoch = meta?.FeedEpoch ?? Guid.Empty;
        var oldest = await db.ChangeLog.OrderBy(c => c.Seq).Select(c => (long?)c.Seq).FirstOrDefaultAsync(ct);
        if (since is { } s && oldest is { } o && s < o)
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        var query = db.ChangeLog.AsNoTracking().OrderBy(c => c.Seq).AsQueryable();
        if (since is { } sinceSeq)
        {
            query = query.Where(c => c.Seq > sinceSeq);
        }

        var rows = await query.Take(500).ToListAsync(ct);
        return Results.Ok(new
        {
            feed_epoch = feedEpoch,
            warehouse,
            entries = rows.Select(r => new
            {
                r.Seq,
                r.Entity,
                r.Id,
                r.Op,
                payload = JsonDocument.Parse(r.Payload).RootElement,
                r.CommandId,
                r.Actor,
                r.OccurredAt,
                r.RecordedAt
            })
        });
    }

    private static async Task<IResult> GetSnapshot(
        Guid tenantId,
        Guid? warehouse,
        string? entity,
        int? page,
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
        var meta = await db.TenantMeta.SingleOrDefaultAsync(ct);
        var pageSize = 100;
        var pageNum = page ?? 1;
        var skip = (pageNum - 1) * pageSize;
        var kind = string.IsNullOrWhiteSpace(entity) ? "Task" : entity;

        object items;
        if (kind.Equals("Location", StringComparison.OrdinalIgnoreCase))
        {
            var q = db.Locations.AsNoTracking().AsQueryable();
            if (warehouse is { } wh)
            {
                q = q.Where(l => l.WarehouseId == wh);
            }

            items = await q.OrderBy(l => l.Path).ThenBy(l => l.Code)
                .Skip(skip).Take(pageSize)
                .Select(l => new LocationDto(
                    l.Id, l.WarehouseId, l.ParentId, l.Type, l.Code, l.Path,
                    l.HeightMm, l.WidthMm, l.DepthMm, l.MaxWeightG,
                    l.Barcode, l.Status, l.IsSystem))
                .ToListAsync(ct);
            kind = "Location";
        }
        else if (kind.Equals("Warehouse", StringComparison.OrdinalIgnoreCase))
        {
            var q = db.Warehouses.AsNoTracking().AsQueryable();
            if (warehouse is { } wh)
            {
                q = q.Where(w => w.Id == wh);
            }

            items = await q.OrderBy(w => w.Name)
                .Skip(skip).Take(pageSize)
                .Select(w => new WarehouseListDto(w.Id, w.Name, w.CodePattern, w.ActivatedAt))
                .ToListAsync(ct);
            kind = "Warehouse";
        }
        else if (kind.Equals("Article", StringComparison.OrdinalIgnoreCase))
        {
            var articles = await db.Articles.AsNoTracking()
                .OrderBy(a => a.Sku)
                .Skip(skip).Take(pageSize)
                .ToListAsync(ct);
            items = await ArticleApi.WithLevelsAsync(db, articles, ct);
            kind = "Article";
        }
        else if (kind.Equals("UnitOfMeasure", StringComparison.OrdinalIgnoreCase))
        {
            var units = await db.UnitsOfMeasure.AsNoTracking()
                .OrderBy(u => u.Code)
                .Skip(skip).Take(pageSize)
                .ToListAsync(ct);
            items = units.Select(ArticleApi.ToUnitDto).ToList();
            kind = "UnitOfMeasure";
        }
        else if (kind.Equals("HandlingUnit", StringComparison.OrdinalIgnoreCase))
        {
            var q = db.HandlingUnits.AsNoTracking().AsQueryable();
            if (warehouse is { } huWh)
            {
                q = q.Where(h => h.WarehouseId == huWh);
            }

            var hus = await q.OrderBy(h => h.Lpn).Skip(skip).Take(pageSize).ToListAsync(ct);
            var ids = hus.Select(h => h.Id).ToList();
            var contents = await db.HandlingUnitContents.AsNoTracking()
                .Where(c => ids.Contains(c.HandlingUnitId))
                .ToListAsync(ct);
            var byHu = contents.GroupBy(c => c.HandlingUnitId)
                .ToDictionary(g => g.Key, g => g.ToArray());
            items = hus.Select(h => new HandlingUnitDto(
                h.Id,
                h.WarehouseId,
                h.Lpn,
                h.HeightMm,
                h.ReceivedAt,
                (byHu.GetValueOrDefault(h.Id) ?? [])
                    .Select(c => new HandlingUnitContentDto(
                        c.Id,
                        c.HandlingUnitId,
                        c.ArticleId,
                        DecimalString(c.QtyBase),
                        c.PackagingLevelId))
                    .ToArray())).ToList();
            kind = "HandlingUnit";
        }
        else if (kind.Equals("StockBalance", StringComparison.OrdinalIgnoreCase))
        {
            var q = db.StockBalances.AsNoTracking().AsQueryable();
            if (warehouse is { } balWh)
            {
                q = q.Where(b => db.Locations.Any(l => l.Id == b.LocationId && l.WarehouseId == balWh));
            }

            var rows = await q.Skip(skip).Take(pageSize).ToListAsync(ct);
            items = rows.Select(b => new StockBalanceDto(
                b.LocationId,
                b.ArticleId,
                b.HandlingUnitId,
                DecimalString(b.QtyBase),
                DecimalString(b.ReservedQtyBase))).ToList();
            kind = "StockBalance";
        }
        else if (kind.Equals("Order", StringComparison.OrdinalIgnoreCase))
        {
            var q = db.OutboundOrders.AsNoTracking().AsQueryable();
            if (warehouse is { } orderWh)
            {
                q = q.Where(o => o.WarehouseId == orderWh);
            }

            var orders = await q.OrderByDescending(o => o.Id).Skip(skip).Take(pageSize).ToListAsync(ct);
            var ids = orders.Select(o => o.Id).ToList();
            var lines = ids.Count == 0
                ? []
                : await db.OutboundOrderLines.AsNoTracking()
                    .Where(l => ids.Contains(l.OrderId))
                    .ToListAsync(ct);
            var byOrder = lines.ToLookup(l => l.OrderId);
            items = orders.Select(o => new OrderDto(
                o.Id,
                o.WarehouseId,
                o.Source,
                o.ExternalRef,
                o.DestinationName,
                o.RequestedShipDate,
                o.Status,
                o.Notes,
                byOrder[o.Id].Select(l => new OrderLineDto(
                    l.Id,
                    l.OrderId,
                    l.ArticleId,
                    DecimalString(l.RequestedQtyBase),
                    l.RequestedLevelId,
                    DecimalString(l.AllocatedQtyBase),
                    DecimalString(l.TolerancePct),
                    l.Status)).ToArray())).ToList();
            kind = "Order";
        }
        else
        {
            var q = db.Tasks.AsNoTracking().AsQueryable();
            if (warehouse is { } wh)
            {
                q = q.Where(t => t.WarehouseId == wh);
            }

            var tasks = await q.OrderBy(t => t.CreatedAt).Skip(skip).Take(pageSize).ToListAsync(ct);
            var taskIds = tasks.Select(t => t.Id).ToList();
            var lines = taskIds.Count == 0
                ? []
                : await db.TaskLines.AsNoTracking()
                    .Where(l => taskIds.Contains(l.TaskId))
                    .ToListAsync(ct);
            var lineByTask = lines.ToLookup(l => l.TaskId);
            items = tasks.Select(t =>
            {
                var line = lineByTask[t.Id].FirstOrDefault();
                Guid? orderId = null;
                if (line?.SuggestedBreakdown is { Length: > 0 } raw)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.TryGetProperty("order_id", out var prop)
                            && Guid.TryParse(prop.GetString(), out var parsed))
                        {
                            orderId = parsed;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                return new
                {
                    id = t.Id,
                    warehouse_id = t.WarehouseId,
                    type = t.Type,
                    status = t.Status,
                    assignee_user_id = t.AssigneeUserId,
                    assigned_until = t.AssignedUntil,
                    suggested_location_id = t.SuggestedLocationId,
                    created_at = t.CreatedAt,
                    requested_qty_base = line is null ? null : DecimalString(line.RequestedQtyBase),
                    order_id = orderId
                };
            }).ToList();
            kind = "Task";
        }

        return Results.Ok(new
        {
            feed_epoch = meta?.FeedEpoch ?? Guid.Empty,
            warehouse,
            entity = kind,
            page = pageNum,
            snapshot_schema = SchemaVersions.RequiredFromAssembly(),
            items
        });
    }

    private static IResult GetCompat(CommandRegistry registry, IConfiguration config)
    {
        var latest = config["Compat:LatestAppVersion"] ?? "0.0.1";
        return Results.Ok(new
        {
            min_command_versions = registry.MinCommandVersions,
            latest_app_version = latest
        });
    }

    private static string DecimalString(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);
}

public sealed record CommandBatchRequest(
    Guid TenantId,
    DateTimeOffset Now,
    List<CommandEnvelopeDto> Commands);

public sealed record CommandEnvelopeDto(
    Guid Id,
    string Type,
    int V,
    JsonElement Payload,
    DateTimeOffset OccurredAt,
    Guid DeviceId,
    Guid UserId);
