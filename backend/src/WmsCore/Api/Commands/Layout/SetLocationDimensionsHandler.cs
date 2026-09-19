using System.Text.Json;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Commands.Layout;

public sealed record SetLocationDimensionsPayload(
    List<Guid> Ids,
    int? HeightMm,
    int? WidthMm,
    int? DepthMm,
    int? MaxWeightG);

public sealed class SetLocationDimensionsValidator : AbstractValidator<SetLocationDimensionsPayload>
{
    public SetLocationDimensionsValidator()
    {
        RuleFor(x => x.Ids).NotEmpty();
        RuleForEach(x => x.Ids).NotEmpty();
        RuleFor(x => x)
            .Must(x => x.HeightMm is not null || x.WidthMm is not null || x.DepthMm is not null || x.MaxWeightG is not null)
            .WithMessage("at least one dimension required");
        RuleFor(x => x.HeightMm).GreaterThanOrEqualTo(0).When(x => x.HeightMm is not null);
        RuleFor(x => x.WidthMm).GreaterThanOrEqualTo(0).When(x => x.WidthMm is not null);
        RuleFor(x => x.DepthMm).GreaterThanOrEqualTo(0).When(x => x.DepthMm is not null);
        RuleFor(x => x.MaxWeightG).GreaterThanOrEqualTo(0).When(x => x.MaxWeightG is not null);
    }
}

public sealed class SetLocationDimensionsHandler(
    IClock clock,
    IValidator<SetLocationDimensionsPayload> validator) : ICommandHandler
{
    public string Type => "SetLocationDimensions";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(
        CommandEnvelope command,
        CommandContext context,
        TenantDbAccessor db,
        CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<SetLocationDimensionsPayload>(LayoutJson.Options)
            ?? throw new InvalidOperationException("invalid SetLocationDimensions payload");
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        var ids = payload.Ids.Distinct().ToList();
        var rows = await db.Db.Locations.Where(l => ids.Contains(l.Id)).ToListAsync(ct);
        if (rows.Count != ids.Count)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_location", "location not found");
        }

        foreach (var warehouseId in rows.Select(r => r.WarehouseId).Distinct())
        {
            if (!CanMap(context, warehouseId))
            {
                return new CommandResult(command.Id, CommandOutcome.Rejected, "forbidden", "layout.map required");
            }
        }

        var now = clock.UtcNow;
        foreach (var row in rows)
        {
            if (payload.HeightMm is { } h)
            {
                row.HeightMm = h;
            }

            if (payload.WidthMm is { } w)
            {
                row.WidthMm = w;
            }

            if (payload.DepthMm is { } d)
            {
                row.DepthMm = d;
            }

            if (payload.MaxWeightG is { } g)
            {
                row.MaxWeightG = g;
            }

            WriteChangeLog(db, command, context, row, now);
        }

        await db.Db.SaveChangesAsync(ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }

    private static void WriteChangeLog(
        TenantDbAccessor db,
        CommandEnvelope command,
        CommandContext context,
        Location row,
        DateTimeOffset now)
    {
            var json = JsonSerializer.Serialize(new
        {
            id = row.Id,
            warehouse_id = row.WarehouseId,
            parent_id = row.ParentId,
            type = row.Type,
            code = row.Code,
            path = row.Path,
            height_mm = row.HeightMm,
            width_mm = row.WidthMm,
            depth_mm = row.DepthMm,
            max_weight_g = row.MaxWeightG,
            barcode = row.Barcode,
            status = row.Status,
            is_system = row.IsSystem
        }, LayoutJson.Options);

        db.Db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = "location",
            Id = row.Id,
            Op = "upsert",
            Payload = json,
            CommandId = command.Id,
            Actor = context.UserId,
            OccurredAt = context.OccurredAt,
            RecordedAt = now
        });
    }

    private static bool CanMap(CommandContext context, Guid warehouseId) =>
        context.Memberships.Any(m =>
            m.UserId == context.UserId
            && m.ValidFrom <= context.OccurredAt
            && (m.ValidTo is null || m.ValidTo > context.OccurredAt)
            && Permissions.ForRole(m.Role).Contains(Permissions.LayoutMap)
            && (m.WarehouseId is null || m.WarehouseId == warehouseId));
}
