using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Inventory.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Commands.Inventory;

internal static class InventoryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record ReceiveHandlingUnitPayload(
    Guid WarehouseId,
    Guid HandlingUnitId,
    Guid TaskId,
    string Lpn,
    Guid ArticleId,
    string QtyBase,
    Guid? PackagingLevelId,
    int? HeightMm);

public sealed class ReceiveHandlingUnitValidator : AbstractValidator<ReceiveHandlingUnitPayload>
{
    public ReceiveHandlingUnitValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.HandlingUnitId).NotEmpty();
        RuleFor(x => x.TaskId).NotEmpty();
        RuleFor(x => x.Lpn).NotEmpty().MaximumLength(32);
        RuleFor(x => x.ArticleId).NotEmpty();
        RuleFor(x => x.QtyBase).NotEmpty();
    }
}

public sealed class ReceiveHandlingUnitHandler(
    IClock clock,
    IValidator<ReceiveHandlingUnitPayload> validator) : ICommandHandler
{
    public string Type => "ReceiveHandlingUnit";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(
        CommandEnvelope command,
        CommandContext context,
        TenantDbAccessor db,
        CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<ReceiveHandlingUnitPayload>(InventoryJson.Options)
            ?? throw new InvalidOperationException("invalid ReceiveHandlingUnit payload");
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        var warehouse = await db.Db.Warehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == payload.WarehouseId, ct);
        if (warehouse is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_warehouse", "warehouse not found");
        }

        if (!HasPermission(context, payload.WarehouseId, Permissions.InboundReceive))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "forbidden", "inbound.receive required");
        }

        var article = await db.Db.Articles.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == payload.ArticleId, ct);
        if (article is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_article", "article not found");
        }

        if (!decimal.TryParse(payload.QtyBase, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty)
            || qty <= 0
            || decimal.Round(qty, article.QuantityPrecision) != qty
            || qty / article.QuantityStep != decimal.Truncate(qty / article.QuantityStep))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "invalid_qty", payload.QtyBase);
        }

        var receiving = await db.Db.Locations.AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.WarehouseId == payload.WarehouseId && l.IsSystem && l.Code == "RECEIVING",
                ct);
        if (receiving is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_location", "RECEIVING not found");
        }

        var levels = await db.Db.PackagingLevels.AsNoTracking()
            .Where(p => p.ArticleId == article.Id)
            .OrderBy(p => p.Rank)
            .ToListAsync(ct);
        PackagingLevel? level;
        if (payload.PackagingLevelId is { } levelId && levelId != Guid.Empty)
        {
            level = levels.FirstOrDefault(p => p.Id == levelId);
            if (level is null)
            {
                return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_packaging_level", "packaging level not found");
            }
        }
        else
        {
            level = levels.FirstOrDefault(p => p.Rank == 1) ?? levels.FirstOrDefault();
            if (level is null)
            {
                return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_packaging_level", "article has no packaging level");
            }
        }

        var lpn = payload.Lpn.Trim();
        if (await db.Db.HandlingUnits.AnyAsync(
                h => h.WarehouseId == payload.WarehouseId && h.Lpn == lpn, ct))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "duplicate_lpn", lpn);
        }

        var uom = await db.Db.UnitsOfMeasure.AsNoTracking()
            .FirstAsync(u => u.Id == article.BaseUomId, ct);
        var now = clock.UtcNow;
        var suggested = await FirstEmptyBinAsync(db, payload.WarehouseId, now, ct);

        var hu = new HandlingUnit
        {
            Id = payload.HandlingUnitId,
            WarehouseId = payload.WarehouseId,
            Lpn = lpn,
            HeightMm = payload.HeightMm,
            ReceivedAt = context.OccurredAt
        };
        var content = new HandlingUnitContent
        {
            Id = Ids.New(),
            HandlingUnitId = hu.Id,
            ArticleId = article.Id,
            QtyBase = qty,
            PackagingLevelId = level.Id
        };
        var movement = new StockMovement
        {
            Id = Ids.New(),
            OccurredAt = context.OccurredAt,
            RecordedAt = now,
            ActorUserId = context.UserId,
            DeviceId = context.DeviceId,
            CommandId = command.Id,
            ArticleId = article.Id,
            ToLocationId = receiving.Id,
            ToHandlingUnitId = hu.Id,
            QtyBase = qty,
            BaseUomCode = uom.Code,
            EnteredQty = qty,
            EnteredLevelId = level.Id,
            EnteredUomId = article.BaseUomId,
            Reason = "receive",
            ReferenceType = "task",
            ReferenceId = payload.TaskId
        };
        var balance = new StockBalance
        {
            LocationId = receiving.Id,
            ArticleId = article.Id,
            HandlingUnitId = hu.Id,
            QtyBase = qty,
            ReservedQtyBase = 0
        };
        var task = new WarehouseTask
        {
            Id = payload.TaskId,
            WarehouseId = payload.WarehouseId,
            Type = "putaway",
            Status = "open",
            SuggestedLocationId = suggested?.Id,
            CreatedAt = now
        };
        var line = new TaskLine
        {
            Id = Ids.New(),
            TaskId = task.Id,
            ArticleId = article.Id,
            RequestedQtyBase = qty,
            FromLocationId = receiving.Id,
            FromHandlingUnitId = hu.Id,
            Status = "open"
        };

        db.Db.HandlingUnits.Add(hu);
        db.Db.HandlingUnitContents.Add(content);
        db.Db.StockMovements.Add(movement);
        db.Db.StockBalances.Add(balance);
        db.Db.Tasks.Add(task);
        db.Db.TaskLines.Add(line);
        if (suggested is not null)
        {
            db.Db.LocationReservations.Add(new LocationReservation
            {
                LocationId = suggested.Id,
                TaskId = task.Id,
                ExpiresAt = now.AddMinutes(warehouse.ClaimMinutes)
            });
        }

        var huDto = new HandlingUnitDto(
            hu.Id,
            hu.WarehouseId,
            hu.Lpn,
            hu.HeightMm,
            hu.ReceivedAt,
            [new HandlingUnitContentDto(content.Id, hu.Id, article.Id, DecimalString(qty), level.Id)]);
        var huJson = JsonSerializer.Serialize(huDto, InventoryJson.Options);
        WriteLog(db, command, context, now, "handling_unit", hu.Id, huJson);
        WriteLog(db, command, context, now, "stock_balance", hu.Id, JsonSerializer.Serialize(
            new StockBalanceDto(receiving.Id, article.Id, hu.Id, DecimalString(qty), "0"),
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
        db.Db.Outbox.Add(new OutboxRow
        {
            Id = Ids.New(),
            Type = "inventory.received",
            Payload = huJson,
            OccurredAt = context.OccurredAt
        });
        await db.Db.SaveChangesAsync(ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }

    private static async Task<Location?> FirstEmptyBinAsync(
        TenantDbAccessor db,
        Guid warehouseId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var occupied = db.Db.StockBalances.Where(b => b.QtyBase > 0).Select(b => b.LocationId);
        var reserved = db.Db.LocationReservations.Where(r => r.ExpiresAt > now).Select(r => r.LocationId);
        return await db.Db.Locations
            .Where(l => l.WarehouseId == warehouseId
                && l.Type == "bin"
                && !l.IsSystem
                && l.Status == "active"
                && !occupied.Contains(l.Id)
                && !reserved.Contains(l.Id))
            .OrderBy(l => l.Code)
            .FirstOrDefaultAsync(ct);
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
