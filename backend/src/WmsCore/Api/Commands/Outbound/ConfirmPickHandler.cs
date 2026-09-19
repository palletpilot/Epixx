using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands.Inventory;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Inventory.Contracts;
using Lagerkraft.WmsCore.Outbound.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Commands.Outbound;

public sealed record ConfirmPickPayload(Guid TaskId, Guid ToteId, string ToteLpn, string Qty);

public sealed class ConfirmPickValidator : AbstractValidator<ConfirmPickPayload>
{
    public ConfirmPickValidator()
    {
        RuleFor(x => x.TaskId).NotEmpty();
        RuleFor(x => x.ToteId).NotEmpty();
        RuleFor(x => x.ToteLpn).NotEmpty();
        RuleFor(x => x.Qty).NotEmpty();
    }
}

public sealed class ConfirmPickHandler(
    IClock clock,
    IValidator<ConfirmPickPayload> validator) : ICommandHandler
{
    public string Type => "ConfirmPick";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(
        CommandEnvelope command,
        CommandContext context,
        TenantDbAccessor db,
        CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<ConfirmPickPayload>(InventoryJson.Options)
            ?? throw new InvalidOperationException("invalid ConfirmPick payload");
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        var task = await db.Db.Tasks.FirstOrDefaultAsync(t => t.Id == payload.TaskId, ct);
        if (task is null || task.Type != "pick")
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_task", "pick task not found");
        }

        if (task.Status == "done")
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "invalid_status", task.Status);
        }

        if (!HasPermission(context, task.WarehouseId, Permissions.OutboundPick))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "forbidden", "outbound.pick required");
        }

        var line = await db.Db.TaskLines.FirstOrDefaultAsync(l => l.TaskId == task.Id, ct);
        if (line?.FromHandlingUnitId is not { } huId || line.FromLocationId is not { } fromId
            || line.ArticleId is not { } articleId)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_task", "pick line missing");
        }

        if (!decimal.TryParse(payload.Qty, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty)
            || qty != line.RequestedQtyBase)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "qty_mismatch", payload.Qty);
        }

        // ponytail: pick task has no order_id; match oldest allocated order for this article. Upgrade: store order_id on Task.
        var orderMatch = await (
            from ol in db.Db.OutboundOrderLines
            join o in db.Db.OutboundOrders on ol.OrderId equals o.Id
            where o.WarehouseId == task.WarehouseId
                && o.Status == "allocated"
                && ol.ArticleId == articleId
                && ol.Status == "open"
            orderby o.Id
            select new { Order = o, Line = ol }).FirstOrDefaultAsync(ct);
        if (orderMatch is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_task", "allocated order not found");
        }

        var source = await db.Db.StockBalances.FirstOrDefaultAsync(
            b => b.LocationId == fromId && b.HandlingUnitId == huId && b.ArticleId == articleId, ct);
        if (source is null || source.QtyBase < qty)
        {
            await ReallocateIfPossibleAsync(db, task, line, articleId, qty, huId, source, ct);
            var nowShort = clock.UtcNow;
            db.Db.Deviations.Add(new Deviation
            {
                Id = Ids.New(),
                Kind = "short_pick",
                CommandId = command.Id,
                Detail = JsonSerializer.Serialize(new
                {
                    task_id = task.Id,
                    from_location_id = fromId,
                    from_handling_unit_id = huId,
                    article_id = articleId,
                    qty_base = DecimalString(qty)
                }, InventoryJson.Options),
                CreatedAt = nowShort
            });
            await db.Db.SaveChangesAsync(ct);
            return new CommandResult(command.Id, CommandOutcome.Rejected, "short_pick", "source remaining below qty");
        }

        var picking = await db.Db.Locations.FirstOrDefaultAsync(
            l => l.WarehouseId == task.WarehouseId && l.Code == "PICKING" && l.IsSystem, ct);
        if (picking is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_location", "PICKING not found");
        }

        var content = await db.Db.HandlingUnitContents.FirstOrDefaultAsync(c => c.HandlingUnitId == huId, ct);
        if (content is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "hu_moved", "handling unit has no content");
        }

        var article = await db.Db.Articles.AsNoTracking().FirstAsync(a => a.Id == articleId, ct);
        var uom = await db.Db.UnitsOfMeasure.AsNoTracking().FirstAsync(u => u.Id == article.BaseUomId, ct);
        var packagingLevelId = content.PackagingLevelId;
        var now = clock.UtcNow;

        var tote = await db.Db.HandlingUnits.FirstOrDefaultAsync(h => h.Id == payload.ToteId, ct);
        if (tote is null)
        {
            if (await db.Db.HandlingUnits.AnyAsync(
                    h => h.WarehouseId == task.WarehouseId && h.Lpn == payload.ToteLpn, ct))
            {
                return new CommandResult(command.Id, CommandOutcome.Rejected, "duplicate_lpn", payload.ToteLpn);
            }

            tote = new HandlingUnit
            {
                Id = payload.ToteId,
                WarehouseId = task.WarehouseId,
                Lpn = payload.ToteLpn,
                ReceivedAt = now
            };
            db.Db.HandlingUnits.Add(tote);
        }

        var toteContent = await db.Db.HandlingUnitContents.FirstOrDefaultAsync(
            c => c.HandlingUnitId == tote.Id && c.ArticleId == articleId, ct);
        if (toteContent is null)
        {
            toteContent = new HandlingUnitContent
            {
                Id = Ids.New(),
                HandlingUnitId = tote.Id,
                ArticleId = articleId,
                QtyBase = qty,
                PackagingLevelId = content.PackagingLevelId
            };
            db.Db.HandlingUnitContents.Add(toteContent);
        }
        else
        {
            toteContent.QtyBase += qty;
        }

        content.QtyBase -= qty;
        if (content.QtyBase == 0)
        {
            db.Db.HandlingUnitContents.Remove(content);
        }

        db.Db.StockMovements.Add(new StockMovement
        {
            Id = Ids.New(),
            OccurredAt = context.OccurredAt,
            RecordedAt = now,
            ActorUserId = context.UserId,
            DeviceId = context.DeviceId,
            CommandId = command.Id,
            ArticleId = articleId,
            FromLocationId = fromId,
            ToLocationId = picking.Id,
            FromHandlingUnitId = huId,
            ToHandlingUnitId = tote.Id,
            QtyBase = qty,
            BaseUomCode = uom.Code,
            EnteredQty = qty,
            EnteredLevelId = packagingLevelId,
            EnteredUomId = article.BaseUomId,
            Reason = "pick",
            ReferenceType = "task",
            ReferenceId = task.Id
        });

        source.QtyBase -= qty;
        source.ReservedQtyBase = Math.Max(0, source.ReservedQtyBase - qty);
        if (source.QtyBase == 0)
        {
            db.Db.StockBalances.Remove(source);
        }

        var destBal = await db.Db.StockBalances.FindAsync([picking.Id, articleId, tote.Id], ct);
        if (destBal is null)
        {
            destBal = new StockBalance
            {
                LocationId = picking.Id,
                ArticleId = articleId,
                HandlingUnitId = tote.Id,
                QtyBase = 0,
                ReservedQtyBase = 0
            };
            db.Db.StockBalances.Add(destBal);
        }

        destBal.QtyBase += qty;

        task.Status = "done";
        line.Status = "done";
        line.PickedQtyBase = qty;

        var reservations = await db.Db.LocationReservations.Where(r => r.TaskId == task.Id).ToListAsync(ct);
        db.Db.LocationReservations.RemoveRange(reservations);

        orderMatch.Line.Status = "picked";
        orderMatch.Order.Status = "picked";

        var toteContents = await db.Db.HandlingUnitContents.Where(c => c.HandlingUnitId == tote.Id).ToListAsync(ct);
        if (!toteContents.Contains(toteContent) && toteContent.QtyBase > 0)
        {
            toteContents.Add(toteContent);
        }

        var toteJson = JsonSerializer.Serialize(
            new HandlingUnitDto(
                tote.Id,
                tote.WarehouseId,
                tote.Lpn,
                tote.HeightMm,
                tote.ReceivedAt,
                toteContents.Select(c => new HandlingUnitContentDto(
                    c.Id, c.HandlingUnitId, c.ArticleId, DecimalString(c.QtyBase), c.PackagingLevelId)).ToArray()),
            InventoryJson.Options);
        WriteLog(db, command, context, now, "handling_unit", tote.Id, toteJson);
        WriteLog(db, command, context, now, "stock_balance", tote.Id, JsonSerializer.Serialize(
            new StockBalanceDto(picking.Id, articleId, tote.Id, DecimalString(destBal.QtyBase), DecimalString(destBal.ReservedQtyBase)),
            InventoryJson.Options));
        WriteLog(db, command, context, now, "task", task.Id, JsonSerializer.Serialize(new
        {
            id = task.Id,
            warehouse_id = task.WarehouseId,
            type = task.Type,
            status = task.Status,
            assignee_user_id = task.AssigneeUserId,
            assigned_until = task.AssignedUntil,
            suggested_location_id = task.SuggestedLocationId,
            created_at = task.CreatedAt
        }, InventoryJson.Options));
        WriteLog(db, command, context, now, "order", orderMatch.Order.Id, JsonSerializer.Serialize(
            new OrderDto(
                orderMatch.Order.Id,
                orderMatch.Order.WarehouseId,
                orderMatch.Order.Source,
                orderMatch.Order.ExternalRef,
                orderMatch.Order.DestinationName,
                orderMatch.Order.RequestedShipDate,
                orderMatch.Order.Status,
                orderMatch.Order.Notes,
                [new OrderLineDto(
                    orderMatch.Line.Id,
                    orderMatch.Line.OrderId,
                    orderMatch.Line.ArticleId,
                    DecimalString(orderMatch.Line.RequestedQtyBase),
                    orderMatch.Line.RequestedLevelId,
                    DecimalString(orderMatch.Line.AllocatedQtyBase),
                    DecimalString(orderMatch.Line.TolerancePct),
                    orderMatch.Line.Status)]),
            InventoryJson.Options));
        await db.Db.SaveChangesAsync(ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }

    private static async Task ReallocateIfPossibleAsync(
        TenantDbAccessor db,
        WarehouseTask task,
        TaskLine line,
        Guid articleId,
        decimal qty,
        Guid currentHuId,
        StockBalance? source,
        CancellationToken ct)
    {
        if (source is not null)
        {
            source.ReservedQtyBase = Math.Max(0, source.ReservedQtyBase - qty);
        }

        var next = await (
            from b in db.Db.StockBalances
            join loc in db.Db.Locations on b.LocationId equals loc.Id
            join hu in db.Db.HandlingUnits on b.HandlingUnitId equals hu.Id
            where loc.WarehouseId == task.WarehouseId
                && !loc.IsSystem
                && loc.Code != "QUARANTINE"
                && (loc.Status == null || loc.Status == "active")
                && b.ArticleId == articleId
                && b.HandlingUnitId != currentHuId
                && b.QtyBase - b.ReservedQtyBase >= qty
            orderby hu.ReceivedAt, loc.Code
            select new { b.LocationId, b.HandlingUnitId }).FirstOrDefaultAsync(ct);
        if (next is null)
        {
            return;
        }

        var nextBal = await db.Db.StockBalances.FindAsync([next.LocationId, articleId, next.HandlingUnitId], ct);
        if (nextBal is null)
        {
            return;
        }

        nextBal.ReservedQtyBase += qty;
        line.FromLocationId = next.LocationId;
        line.FromHandlingUnitId = next.HandlingUnitId;
        task.SuggestedLocationId = next.LocationId;
    }

    private static void WriteLog(
        TenantDbAccessor db,
        CommandEnvelope command,
        CommandContext context,
        DateTimeOffset now,
        string entity,
        Guid id,
        string payload)
    {
        db.Db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = entity,
            Id = id,
            Op = "upsert",
            Payload = payload,
            CommandId = command.Id,
            Actor = context.UserId,
            OccurredAt = context.OccurredAt,
            RecordedAt = now
        });
    }

    private static bool HasPermission(CommandContext context, Guid warehouseId, string permission) =>
        context.Memberships.Any(m =>
            m.UserId == context.UserId
            && m.ValidFrom <= context.OccurredAt
            && (m.ValidTo is null || m.ValidTo > context.OccurredAt)
            && Permissions.ForRole(m.Role).Contains(permission)
            && (m.WarehouseId is null || m.WarehouseId == warehouseId));

    private static string DecimalString(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
