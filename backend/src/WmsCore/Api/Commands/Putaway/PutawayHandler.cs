using System.Text.Json;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Commands.Putaway;

public sealed record ConfirmPutawayPayload(
    Guid TaskId,
    Guid LocationId,
    Guid HandlingUnitId,
    string Lpn);

public sealed class ConfirmPutawayValidator : AbstractValidator<ConfirmPutawayPayload>
{
    public ConfirmPutawayValidator()
    {
        RuleFor(x => x.TaskId).NotEmpty();
        RuleFor(x => x.LocationId).NotEmpty();
        RuleFor(x => x.HandlingUnitId).NotEmpty();
        RuleFor(x => x.Lpn).NotEmpty().MaximumLength(64);
    }
}

public sealed class ConfirmPutawayHandler(IClock clock, IValidator<ConfirmPutawayPayload> validator) : ICommandHandler
{
    public string Type => "ConfirmPutaway";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(
        CommandEnvelope command,
        CommandContext context,
        TenantDbAccessor db,
        CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<ConfirmPutawayPayload>(PutawayJson.Options)
            ?? throw new InvalidOperationException("invalid ConfirmPutaway payload");
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        var task = await db.Db.Tasks.FirstOrDefaultAsync(t => t.Id == payload.TaskId, ct);
        if (task is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_task", "task not found");
        }

        var location = await db.Db.Locations.FirstOrDefaultAsync(l => l.Id == payload.LocationId, ct);
        if (location is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_location", "location not found");
        }

        var now = clock.UtcNow;

        var existingOccupant = await db.Db.Stock
            .Where(s => s.LocationId == payload.LocationId && s.HandlingUnitId != null)
            .FirstOrDefaultAsync(ct);

        Deviation? deviation = null;
        HandlingUnit? previousHu = null;
        if (existingOccupant is not null)
        {
            previousHu = await db.Db.HandlingUnits
                .FirstOrDefaultAsync(h => h.Id == existingOccupant.HandlingUnitId, ct);
            if (previousHu is not null)
            {
                previousHu.LocationId = null;
            }

            deviation = new Deviation
            {
                Id = Ids.New(),
                Kind = "occupied_bin",
                CommandId = command.Id,
                Detail = JsonSerializer.Serialize(new
                {
                    location_id = payload.LocationId,
                    existing_handling_unit_id = existingOccupant.HandlingUnitId,
                    incoming_handling_unit_id = payload.HandlingUnitId,
                    lpn = payload.Lpn
                }, PutawayJson.Options),
                CreatedAt = now
            };
            db.Db.Deviations.Add(deviation);

            var removedStockJson = JsonSerializer.Serialize(new
            {
                id = existingOccupant.Id,
                warehouse_id = existingOccupant.WarehouseId,
                location_id = existingOccupant.LocationId,
                handling_unit_id = existingOccupant.HandlingUnitId,
                article_id = existingOccupant.ArticleId,
                qty_base = existingOccupant.QtyBase,
                created_at = existingOccupant.CreatedAt
            }, PutawayJson.Options);

            db.Db.ChangeLog.Add(new ChangeLogRow
            {
                Entity = "stock",
                Id = existingOccupant.Id,
                Op = "delete",
                Payload = removedStockJson,
                CommandId = command.Id,
                Actor = context.UserId,
                OccurredAt = context.OccurredAt,
                RecordedAt = now
            });

            db.Db.Outbox.Add(new OutboxRow
            {
                Id = Ids.New(),
                Type = "inventory.stock.removed",
                Payload = removedStockJson,
                OccurredAt = context.OccurredAt
            });

            db.Db.Stock.Remove(existingOccupant);
        }

        var hu = new HandlingUnit
        {
            Id = payload.HandlingUnitId,
            WarehouseId = task.WarehouseId,
            Lpn = payload.Lpn,
            LocationId = payload.LocationId
        };
        db.Db.HandlingUnits.Add(hu);

        var stock = new Stock
        {
            Id = Ids.New(),
            WarehouseId = task.WarehouseId,
            LocationId = payload.LocationId,
            HandlingUnitId = payload.HandlingUnitId,
            ArticleId = null,
            QtyBase = 1m,
            CreatedAt = now
        };
        db.Db.Stock.Add(stock);

        var reservation = await db.Db.LocationReservations
            .FirstOrDefaultAsync(r => r.LocationId == payload.LocationId && r.TaskId == payload.TaskId, ct);
        if (reservation is not null)
        {
            reservation.Released = true;
        }

        task.Status = "done";
        task.AssignedUntil = null;

        await WriteSideEffects(db, command, context, task, stock, deviation, now, ct);

        return new CommandResult(command.Id, CommandOutcome.Applied);
    }

    private static async Task WriteSideEffects(
        TenantDbAccessor db,
        CommandEnvelope command,
        CommandContext context,
        WarehouseTask task,
        Stock stock,
        Deviation? deviation,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var taskJson = JsonSerializer.Serialize(new
        {
            id = task.Id,
            warehouse_id = task.WarehouseId,
            type = task.Type,
            status = task.Status,
            assignee_user_id = task.AssigneeUserId,
            assigned_until = task.AssignedUntil,
            suggested_location_id = task.SuggestedLocationId,
            created_at = task.CreatedAt
        }, PutawayJson.Options);

        db.Db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = "task",
            Id = task.Id,
            Op = "upsert",
            Payload = taskJson,
            CommandId = command.Id,
            Actor = context.UserId,
            OccurredAt = context.OccurredAt,
            RecordedAt = now
        });

        var stockJson = JsonSerializer.Serialize(new
        {
            id = stock.Id,
            warehouse_id = stock.WarehouseId,
            location_id = stock.LocationId,
            handling_unit_id = stock.HandlingUnitId,
            article_id = stock.ArticleId,
            qty_base = stock.QtyBase,
            created_at = stock.CreatedAt
        }, PutawayJson.Options);

        db.Db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = "stock",
            Id = stock.Id,
            Op = "upsert",
            Payload = stockJson,
            CommandId = command.Id,
            Actor = context.UserId,
            OccurredAt = context.OccurredAt,
            RecordedAt = now
        });

        db.Db.Outbox.Add(new OutboxRow
        {
            Id = Ids.New(),
            Type = "inventory.task.completed",
            Payload = taskJson,
            OccurredAt = context.OccurredAt
        });

        db.Db.Outbox.Add(new OutboxRow
        {
            Id = Ids.New(),
            Type = "inventory.stock.added",
            Payload = stockJson,
            OccurredAt = context.OccurredAt
        });

        if (deviation is not null)
        {
            var deviationPayload = JsonSerializer.Serialize(new
            {
                id = deviation.Id,
                kind = deviation.Kind,
                command_id = deviation.CommandId,
                detail = JsonDocument.Parse(deviation.Detail).RootElement,
                created_at = deviation.CreatedAt
            }, PutawayJson.Options);

            db.Db.ChangeLog.Add(new ChangeLogRow
            {
                Entity = "deviation",
                Id = deviation.Id,
                Op = "insert",
                Payload = deviationPayload,
                CommandId = command.Id,
                Actor = context.UserId,
                OccurredAt = context.OccurredAt,
                RecordedAt = now
            });

            db.Db.Outbox.Add(new OutboxRow
            {
                Id = Ids.New(),
                Type = "inventory.deviation.reported",
                Payload = deviationPayload,
                OccurredAt = context.OccurredAt
            });
        }

        await db.Db.SaveChangesAsync(ct);
    }
}

file static class PutawayJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
