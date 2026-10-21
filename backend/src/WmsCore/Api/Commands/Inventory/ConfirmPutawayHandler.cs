using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Layout;
using Lagerkraft.WmsCore.Inventory.Contracts;
using Lagerkraft.WmsCore.Layout.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Commands.Inventory;

public sealed record ConfirmPutawayPayload(Guid TaskId, Guid LocationId, string Qty, Guid? NewHuId);

public sealed class ConfirmPutawayValidator : AbstractValidator<ConfirmPutawayPayload>
{
    public ConfirmPutawayValidator()
    {
        RuleFor(x => x.TaskId).NotEmpty();
        RuleFor(x => x.LocationId).NotEmpty();
        RuleFor(x => x.Qty).NotEmpty();
    }
}

public sealed class ConfirmPutawayHandler(
    IClock clock,
    IValidator<ConfirmPutawayPayload> validator) : ICommandHandler
{
    public string Type => "ConfirmPutaway";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(
        CommandEnvelope command,
        CommandContext context,
        TenantDbAccessor db,
        CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<ConfirmPutawayPayload>(InventoryJson.Options)
            ?? throw new InvalidOperationException("invalid ConfirmPutaway payload");
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        var task = await db.Db.Tasks.FirstOrDefaultAsync(t => t.Id == payload.TaskId, ct);
        if (task is null || task.Type != "putaway")
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_task", "putaway task not found");
        }

        if (task.Status == "done")
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "invalid_status", task.Status);
        }

        if (!HasPermission(context, task.WarehouseId, Permissions.InboundPutaway))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "forbidden", "inbound.putaway required");
        }

        var dest = await db.Db.Locations.FirstOrDefaultAsync(l => l.Id == payload.LocationId, ct);
        if (dest is null || dest.WarehouseId != task.WarehouseId || dest.Status != "active"
            || SystemLocations.IsForbiddenPutawayDest(dest))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_location", "location not found");
        }

        var line = await db.Db.TaskLines.FirstOrDefaultAsync(l => l.TaskId == task.Id, ct);
        if (line?.FromHandlingUnitId is not { } huId || line.FromLocationId is not { } fromId)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_task", "putaway line missing");
        }

        var content = await db.Db.HandlingUnitContents.FirstOrDefaultAsync(c => c.HandlingUnitId == huId, ct);
        if (content is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "hu_moved", "handling unit has no content");
        }

        if (!decimal.TryParse(payload.Qty, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty)
            || qty != content.QtyBase)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "qty_mismatch", payload.Qty);
        }

        var source = await db.Db.StockBalances.FirstOrDefaultAsync(
            b => b.LocationId == fromId && b.HandlingUnitId == huId && b.QtyBase > 0, ct);
        if (source is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "hu_moved", "handling unit not at source");
        }

        var article = await db.Db.Articles.AsNoTracking().FirstAsync(a => a.Id == content.ArticleId, ct);
        var uom = await db.Db.UnitsOfMeasure.AsNoTracking().FirstAsync(u => u.Id == article.BaseUomId, ct);
        var hu = await db.Db.HandlingUnits.FirstAsync(h => h.Id == huId, ct);
        var now = clock.UtcNow;

        var movement = new StockMovement
        {
            Id = Ids.New(),
            OccurredAt = context.OccurredAt,
            RecordedAt = now,
            ActorUserId = context.UserId,
            DeviceId = context.DeviceIdOrNull,
            CommandId = command.Id,
            ArticleId = content.ArticleId,
            FromLocationId = fromId,
            ToLocationId = dest.Id,
            FromHandlingUnitId = huId,
            ToHandlingUnitId = huId,
            QtyBase = qty,
            BaseUomCode = uom.Code,
            EnteredQty = qty,
            EnteredLevelId = content.PackagingLevelId,
            EnteredUomId = article.BaseUomId,
            Reason = "putaway",
            ReferenceType = "task",
            ReferenceId = task.Id
        };
        db.Db.StockMovements.Add(movement);

        source.QtyBase -= qty;
        if (source.QtyBase == 0)
        {
            db.Db.StockBalances.Remove(source);
        }

        if (dest.Id != fromId)
        {
            var destBal = await db.Db.StockBalances.FindAsync([dest.Id, content.ArticleId, huId], ct);
            if (destBal is null)
            {
                destBal = new StockBalance
                {
                    LocationId = dest.Id,
                    ArticleId = content.ArticleId,
                    HandlingUnitId = huId,
                    QtyBase = 0,
                    ReservedQtyBase = 0
                };
                db.Db.StockBalances.Add(destBal);
            }

            destBal.QtyBase += qty;
        }

        var occupantBalances = await db.Db.StockBalances
            .Where(b => b.LocationId == dest.Id && b.HandlingUnitId != huId && b.QtyBase > 0)
            .ToListAsync(ct);
        Deviation? deviation = null;
        if (occupantBalances.Count > 0)
        {
            var unlocated = await db.Db.Locations.FirstOrDefaultAsync(
                l => l.WarehouseId == task.WarehouseId && l.IsSystem && l.Code == LayoutDefaults.Unlocated, ct);
            if (unlocated is null)
            {
                return new CommandResult(
                    command.Id, CommandOutcome.Rejected, "unknown_location", $"{LayoutDefaults.Unlocated} not found");
            }

            foreach (var occupant in occupantBalances)
            {
                var occupantContent = await db.Db.HandlingUnitContents.FirstOrDefaultAsync(
                    c => c.HandlingUnitId == occupant.HandlingUnitId && c.ArticleId == occupant.ArticleId, ct);
                var occupantArticle = await db.Db.Articles.AsNoTracking()
                    .FirstAsync(a => a.Id == occupant.ArticleId, ct);
                var occupantUom = await db.Db.UnitsOfMeasure.AsNoTracking()
                    .FirstAsync(u => u.Id == occupantArticle.BaseUomId, ct);
                db.Db.StockMovements.Add(new StockMovement
                {
                    Id = Ids.New(),
                    OccurredAt = context.OccurredAt,
                    RecordedAt = now,
                    ActorUserId = context.UserId,
                    DeviceId = context.DeviceIdOrNull,
                    CommandId = command.Id,
                    ArticleId = occupant.ArticleId,
                    FromLocationId = dest.Id,
                    ToLocationId = unlocated.Id,
                    FromHandlingUnitId = occupant.HandlingUnitId,
                    ToHandlingUnitId = occupant.HandlingUnitId,
                    QtyBase = occupant.QtyBase,
                    BaseUomCode = occupantUom.Code,
                    EnteredQty = occupant.QtyBase,
                    EnteredLevelId = occupantContent?.PackagingLevelId,
                    EnteredUomId = occupantArticle.BaseUomId,
                    Reason = "displaced",
                    ReferenceType = "task",
                    ReferenceId = task.Id
                });

                var unlocatedBal = await db.Db.StockBalances.FindAsync(
                    [unlocated.Id, occupant.ArticleId, occupant.HandlingUnitId], ct);
                if (unlocatedBal is null)
                {
                    unlocatedBal = new StockBalance
                    {
                        LocationId = unlocated.Id,
                        ArticleId = occupant.ArticleId,
                        HandlingUnitId = occupant.HandlingUnitId,
                        QtyBase = 0,
                        ReservedQtyBase = 0
                    };
                    db.Db.StockBalances.Add(unlocatedBal);
                }

                unlocatedBal.QtyBase += occupant.QtyBase;
                unlocatedBal.ReservedQtyBase += occupant.ReservedQtyBase;
                db.Db.StockBalances.Remove(occupant);
                WriteLog(
                    db, command, context, now, "stock_balance", task.WarehouseId,
                    StockBalanceId.For(occupant.HandlingUnitId, occupant.ArticleId),
                    JsonSerializer.Serialize(
                        new StockBalanceDto(
                            StockBalanceId.For(occupant.HandlingUnitId, occupant.ArticleId),
                            unlocated.Id,
                            occupant.ArticleId,
                            occupant.HandlingUnitId,
                            DecimalString(unlocatedBal.QtyBase),
                            DecimalString(unlocatedBal.ReservedQtyBase)),
                        InventoryJson.Options));
            }

            var existingHuId = occupantBalances[0].HandlingUnitId;
            var existingReservedQty = occupantBalances
                .Where(b => b.HandlingUnitId == existingHuId)
                .Sum(b => b.ReservedQtyBase);
            deviation = new Deviation
            {
                Id = Ids.New(),
                WarehouseId = task.WarehouseId,
                Kind = "occupied_bin",
                Status = "open",
                CommandId = command.Id,
                Detail = JsonSerializer.Serialize(new
                {
                    location_id = dest.Id,
                    existing_handling_unit_id = existingHuId,
                    existing_reserved_qty_base = DecimalString(existingReservedQty),
                    incoming_handling_unit_id = huId,
                    lpn = hu.Lpn,
                    command_id = command.Id
                }, InventoryJson.Options),
                CreatedAt = now
            };
            db.Db.Deviations.Add(deviation);
        }
        else if (task.SuggestedLocationId is { } suggested && suggested != dest.Id)
        {
            deviation = new Deviation
            {
                Id = Ids.New(),
                WarehouseId = task.WarehouseId,
                Kind = "wrong_location",
                Status = "open",
                CommandId = command.Id,
                Detail = JsonSerializer.Serialize(new
                {
                    suggested_location_id = suggested,
                    location_id = dest.Id
                }, InventoryJson.Options),
                CreatedAt = now
            };
            db.Db.Deviations.Add(deviation);
        }

        task.Status = "done";
        line.Status = "done";
        line.PickedQtyBase = qty;

        var reservations = await db.Db.LocationReservations.Where(r => r.TaskId == task.Id).ToListAsync(ct);
        db.Db.LocationReservations.RemoveRange(reservations);

        var contents = await db.Db.HandlingUnitContents.Where(c => c.HandlingUnitId == hu.Id).ToListAsync(ct);
        var huJson = JsonSerializer.Serialize(
            new HandlingUnitDto(
                hu.Id,
                hu.WarehouseId,
                hu.Lpn,
                hu.HeightMm,
                hu.ReceivedAt,
                contents.Select(c => new HandlingUnitContentDto(
                    c.Id, c.HandlingUnitId, c.ArticleId, DecimalString(c.QtyBase), c.PackagingLevelId)).ToArray()),
            InventoryJson.Options);
        WriteLog(db, command, context, now, "handling_unit", task.WarehouseId, hu.Id, huJson);
        var destQty = dest.Id == fromId ? source.QtyBase : qty;
        if (dest.Id != fromId)
        {
            var destBalNow = await db.Db.StockBalances.FindAsync([dest.Id, content.ArticleId, huId], ct);
            destQty = destBalNow?.QtyBase ?? qty;
        }

        WriteLog(db, command, context, now, "stock_balance", task.WarehouseId, StockBalanceId.For(huId, content.ArticleId), JsonSerializer.Serialize(
            new StockBalanceDto(StockBalanceId.For(huId, content.ArticleId), dest.Id, content.ArticleId, huId, DecimalString(destQty), "0"),
            InventoryJson.Options));
        WriteLog(db, command, context, now, "task", task.WarehouseId, task.Id, JsonSerializer.Serialize(new
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

        if (deviation is not null)
        {
            var deviationPayload = JsonSerializer.Serialize(new
            {
                id = deviation.Id,
                warehouse_id = deviation.WarehouseId,
                kind = deviation.Kind,
                status = deviation.Status,
                command_id = deviation.CommandId,
                detail = JsonDocument.Parse(deviation.Detail).RootElement,
                created_at = deviation.CreatedAt
            }, InventoryJson.Options);
            WriteLog(db, command, context, now, "deviation", task.WarehouseId, deviation.Id, deviationPayload);
            db.Db.Outbox.Add(new OutboxRow
            {
                Id = Ids.New(),
                Type = "inventory.deviation.reported",
                Payload = deviationPayload,
                OccurredAt = context.OccurredAt
            });
        }

        db.Db.Outbox.Add(new OutboxRow
        {
            Id = Ids.New(),
            Type = "inventory.putaway.confirmed",
            Payload = huJson,
            OccurredAt = context.OccurredAt
        });
        await db.Db.SaveChangesAsync(ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }

    private static void WriteLog(
        TenantDbAccessor db,
        CommandEnvelope command,
        CommandContext context,
        DateTimeOffset now,
        string entity,
        Guid warehouseId,
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
            WarehouseId = warehouseId,
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
