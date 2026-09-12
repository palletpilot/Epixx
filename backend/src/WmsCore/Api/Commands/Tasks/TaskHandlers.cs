using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Commands.Tasks;

internal static class TaskJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record CreateTaskPayload(Guid? Id, Guid WarehouseId, string Type, Guid? SuggestedLocationId);
public sealed record ClaimTaskPayload(Guid TaskId);
public sealed record ReleaseTaskPayload(Guid TaskId);
public sealed record CompleteTaskPayload(Guid TaskId);

public sealed class CreateTaskValidator : AbstractValidator<CreateTaskPayload>
{
    public CreateTaskValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Type).Must(t => t is "putaway" or "pick" or "move" or "count");
    }
}

public sealed class ClaimTaskValidator : AbstractValidator<ClaimTaskPayload>
{
    public ClaimTaskValidator() => RuleFor(x => x.TaskId).NotEmpty();
}

public sealed class ReleaseTaskValidator : AbstractValidator<ReleaseTaskPayload>
{
    public ReleaseTaskValidator() => RuleFor(x => x.TaskId).NotEmpty();
}

public sealed class CompleteTaskValidator : AbstractValidator<CompleteTaskPayload>
{
    public CompleteTaskValidator() => RuleFor(x => x.TaskId).NotEmpty();
}

public sealed class CreateTaskHandler(IClock clock, IValidator<CreateTaskPayload> validator) : ICommandHandler
{
    public string Type => "CreateTask";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(CommandEnvelope command, CommandContext context, TenantDbAccessor db, CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<CreateTaskPayload>(TaskJson.Options)
            ?? throw new InvalidOperationException("invalid CreateTask payload");
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        var warehouse = await db.Db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == payload.WarehouseId, ct);
        if (warehouse is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_warehouse", "warehouse not found");
        }

        if (!IsManager(context))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "forbidden", "create requires manager");
        }

        var id = payload.Id is { } given && given != Guid.Empty ? given : Ids.New();
        var task = new WarehouseTask
        {
            Id = id,
            WarehouseId = payload.WarehouseId,
            Type = payload.Type,
            Status = "open",
            SuggestedLocationId = payload.SuggestedLocationId,
            CreatedAt = clock.UtcNow
        };
        db.Db.Tasks.Add(task);
        await TaskSideEffects.WriteSideEffects(db, command, context, task, "inventory.task.created", clock.UtcNow, ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }

    private static bool IsManager(CommandContext context) =>
        context.Memberships.Any(m =>
            m.UserId == context.UserId
            && m.ValidFrom <= context.OccurredAt
            && (m.ValidTo is null || m.ValidTo > context.OccurredAt)
            && m.Role is "tenant_admin" or "warehouse_manager" or "owner");
}

public sealed class ClaimTaskHandler(IClock clock, IValidator<ClaimTaskPayload> validator) : ICommandHandler
{
    public string Type => "ClaimTask";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(CommandEnvelope command, CommandContext context, TenantDbAccessor db, CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<ClaimTaskPayload>(TaskJson.Options)
            ?? throw new InvalidOperationException("invalid ClaimTask payload");
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        if (IsViewerOnly(context))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "forbidden", "viewer cannot claim");
        }

        var task = await db.Db.Tasks
            .FromSql($"SELECT * FROM task WHERE id = {payload.TaskId} FOR UPDATE SKIP LOCKED")
            .SingleOrDefaultAsync(ct);
        if (task is null)
        {
            var current = await db.Db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == payload.TaskId, ct);
            if (current is null)
            {
                return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_task", "task not found");
            }

            return new CommandResult(command.Id, CommandOutcome.Rejected, "already_claimed", "task is claimed");
        }

        var now = clock.UtcNow;
        if (task.Status == "done" || task.Status == "cancelled")
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "invalid_status", task.Status);
        }

        if (task.Status == "claimed"
            && task.AssignedUntil is { } until
            && until > now
            && task.AssigneeUserId is { } assignee
            && assignee != context.UserId)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "already_claimed", "task is claimed");
        }

        if (task.Status == "claimed"
            && task.AssignedUntil is { } untilSelf
            && untilSelf > now
            && task.AssigneeUserId == context.UserId)
        {
            // already held by this user — treat concurrent duplicate as already_claimed
            return new CommandResult(command.Id, CommandOutcome.Rejected, "already_claimed", "task is claimed");
        }

        var warehouse = await db.Db.Warehouses.AsNoTracking().FirstAsync(w => w.Id == task.WarehouseId, ct);
        task.Status = "claimed";
        task.AssigneeUserId = context.UserId;
        task.AssignedUntil = now.AddMinutes(warehouse.ClaimMinutes <= 0 ? 30 : warehouse.ClaimMinutes);
        await TaskSideEffects.WriteSideEffects(db, command, context, task, "inventory.task.claimed", now, ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }

    private static bool IsViewerOnly(CommandContext context)
    {
        var roles = context.Memberships
            .Where(m => m.UserId == context.UserId
                        && m.ValidFrom <= context.OccurredAt
                        && (m.ValidTo is null || m.ValidTo > context.OccurredAt))
            .Select(m => m.Role)
            .ToList();
        return roles.Count > 0 && roles.All(r => r == "viewer");
    }
}

public sealed class ReleaseTaskHandler(IClock clock, IValidator<ReleaseTaskPayload> validator) : ICommandHandler
{
    public string Type => "ReleaseTask";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(CommandEnvelope command, CommandContext context, TenantDbAccessor db, CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<ReleaseTaskPayload>(TaskJson.Options)
            ?? throw new InvalidOperationException("invalid ReleaseTask payload");
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

        if (task.Status != "claimed")
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "invalid_status", task.Status);
        }

        task.Status = "open";
        task.AssigneeUserId = null;
        task.AssignedUntil = null;
        await TaskSideEffects.WriteSideEffects(db, command, context, task, "inventory.task.released", clock.UtcNow, ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }
}

public sealed class CompleteTaskHandler(IClock clock, IValidator<CompleteTaskPayload> validator) : ICommandHandler
{
    public string Type => "CompleteTask";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(CommandEnvelope command, CommandContext context, TenantDbAccessor db, CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<CompleteTaskPayload>(TaskJson.Options)
            ?? throw new InvalidOperationException("invalid CompleteTask payload");
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

        if (task.Status is not ("open" or "claimed"))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "invalid_status", task.Status);
        }

        task.Status = "done";
        task.AssignedUntil = null;
        await TaskSideEffects.WriteSideEffects(db, command, context, task, "inventory.task.completed", clock.UtcNow, ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }
}

file static class TaskSideEffects
{
    public static async Task WriteSideEffects(
        TenantDbAccessor db,
        CommandEnvelope command,
        CommandContext context,
        WarehouseTask task,
        string eventType,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            id = task.Id,
            warehouse_id = task.WarehouseId,
            type = task.Type,
            status = task.Status,
            assignee_user_id = task.AssigneeUserId,
            assigned_until = task.AssignedUntil,
            suggested_location_id = task.SuggestedLocationId,
            created_at = task.CreatedAt
        }, TaskJson.Options);

        db.Db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = "task",
            Id = task.Id,
            Op = "upsert",
            Payload = json,
            CommandId = command.Id,
            Actor = context.UserId,
            OccurredAt = context.OccurredAt,
            RecordedAt = now
        });
        db.Db.Outbox.Add(new OutboxRow
        {
            Id = Ids.New(),
            Type = eventType,
            Payload = json,
            OccurredAt = context.OccurredAt
        });
        await db.Db.SaveChangesAsync(ct);
    }
}




