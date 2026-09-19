using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Lagerkraft.WmsCore.Layout;
using Lagerkraft.WmsCore.Layout.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Commands.Layout;

internal static class LayoutJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record CreateLocationItem(Guid Id, string Code, Guid? ParentId);

public sealed record CreateLocationBatchPayload(
    Guid WarehouseId,
    Guid? ParentId,
    string Type,
    List<CreateLocationItem> Locations);

public sealed record LocationIdMap(Guid From, Guid To);

public sealed class CreateLocationBatchValidator : AbstractValidator<CreateLocationBatchPayload>
{
    public CreateLocationBatchValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Type).Must(t => t is "zone" or "aisle" or "rack" or "level" or "bin" or "floor" or "dock");
        RuleFor(x => x.Locations).NotEmpty();
        RuleForEach(x => x.Locations).ChildRules(item =>
        {
            item.RuleFor(i => i.Id).NotEmpty();
            item.RuleFor(i => i.Code).NotEmpty().MaximumLength(64);
        });
    }
}

public sealed class CreateLocationBatchHandler(
    IClock clock,
    IValidator<CreateLocationBatchPayload> validator,
    IEntitlementCache entitlements) : ICommandHandler
{
    private static readonly string[] Mappable = ["zone", "aisle", "rack", "level", "bin", "floor", "dock"];

    public string Type => "CreateLocationBatch";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(
        CommandEnvelope command,
        CommandContext context,
        TenantDbAccessor db,
        CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<CreateLocationBatchPayload>(LayoutJson.Options)
            ?? throw new InvalidOperationException("invalid CreateLocationBatch payload");
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        if (!Mappable.Contains(payload.Type))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", "type not mappable");
        }

        var warehouse = await db.Db.Warehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == payload.WarehouseId, ct);
        if (warehouse is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_warehouse", "warehouse not found");
        }

        if (!CanMap(context, payload.WarehouseId))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "forbidden", "layout.map required");
        }

        var pattern = string.IsNullOrWhiteSpace(warehouse.CodePattern)
            ? LayoutDefaults.CodePattern
            : warehouse.CodePattern;

        var batchIds = payload.Locations.Select(l => l.Id).ToHashSet();
        var existing = await db.Db.Locations
            .Where(l => l.WarehouseId == payload.WarehouseId)
            .ToListAsync(ct);
        var byId = existing.ToDictionary(l => l.Id);
        var byCode = existing.ToDictionary(l => l.Code, StringComparer.Ordinal);

        foreach (var item in payload.Locations)
        {
            var parentId = item.ParentId ?? payload.ParentId;
            if (parentId is { } pid
                && !byId.ContainsKey(pid)
                && !batchIds.Contains(pid))
            {
                return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_parent", "parent not found");
            }

            if (!LocationCodePattern.IsValid(pattern, payload.Type, item.Code))
            {
                return new CommandResult(command.Id, CommandOutcome.Rejected, "invalid_code", item.Code);
            }

            if (byCode.TryGetValue(item.Code, out var found)
                && (found.ParentId != parentId || found.Type != payload.Type))
            {
                return new CommandResult(command.Id, CommandOutcome.Rejected, "structural_conflict", item.Code);
            }
        }

        var remaps = new List<LocationIdMap>();
        var toInsert = new List<(CreateLocationItem Item, Guid? ParentId, Location? Existing)>();
        foreach (var item in payload.Locations)
        {
            var parentId = item.ParentId ?? payload.ParentId;
            if (byCode.TryGetValue(item.Code, out var found)
                && found.ParentId == parentId
                && found.Type == payload.Type)
            {
                if (found.Id != item.Id)
                {
                    remaps.Add(new LocationIdMap(item.Id, found.Id));
                }

                toInsert.Add((item, parentId, found));
                continue;
            }

            toInsert.Add((item, parentId, null));
        }

        var newLeaves = toInsert.Count(t =>
            t.Existing is null && payload.Type is "bin" or "floor");
        if (newLeaves > 0 && warehouse.ActivatedAt is not null)
        {
            var entitlement = await entitlements.GetAsync(context.TenantId, ct);
            if (entitlement?.HardCapUnits is { } cap)
            {
                var used = await db.Db.Locations.AsNoTracking()
                    .Where(l => !l.IsSystem && l.Status == "active" && (l.Type == "bin" || l.Type == "floor"))
                    .Join(
                        db.Db.Warehouses.AsNoTracking().Where(w => w.ActivatedAt != null),
                        l => l.WarehouseId,
                        w => w.Id,
                        (l, _) => l)
                    .CountAsync(ct);
                if (used + newLeaves > cap)
                {
                    return new CommandResult(command.Id, CommandOutcome.Held, "location_cap", "active location cap");
                }
            }
        }

        var now = clock.UtcNow;
        foreach (var (item, parentId, current) in toInsert)
        {
            if (current is not null)
            {
                continue;
            }

            var parentPath = parentId is { } pid && byId.TryGetValue(pid, out var parent)
                ? parent.Path
                : parentId is { } pending
                    && toInsert.Any(t => t.Item.Id == pending)
                    ? ResolvePath(toInsert, byId, pending)
                    : "";
            var path = string.IsNullOrEmpty(parentPath)
                ? LocationCodePattern.LtreeLabel(item.Code)
                : parentPath + "." + LocationCodePattern.LtreeLabel(item.Code);
            var row = new Location
            {
                Id = item.Id,
                WarehouseId = payload.WarehouseId,
                ParentId = parentId,
                Type = payload.Type,
                Code = item.Code,
                Path = path,
                Barcode = item.Code,
                Status = "active"
            };
            db.Db.Locations.Add(row);
            byId[row.Id] = row;
            byCode[row.Code] = row;
            WriteFeed(db, command, context, row, now);
        }

        await db.Db.SaveChangesAsync(ct);
        return remaps.Count == 0
            ? new CommandResult(command.Id, CommandOutcome.Applied)
            : new CommandResult(command.Id, CommandOutcome.Applied, Details: new { id_map = remaps });
    }

    private static string ResolvePath(
        List<(CreateLocationItem Item, Guid? ParentId, Location? Existing)> batch,
        Dictionary<Guid, Location> byId,
        Guid id)
    {
        if (byId.TryGetValue(id, out var known))
        {
            return known.Path;
        }

        var node = batch.First(t => t.Item.Id == id);
        if (node.Existing is not null)
        {
            return node.Existing.Path;
        }

        var parentPath = node.ParentId is { } pid ? ResolvePath(batch, byId, pid) : "";
        var label = LocationCodePattern.LtreeLabel(node.Item.Code);
        return string.IsNullOrEmpty(parentPath) ? label : parentPath + "." + label;
    }

    private static void WriteFeed(
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
        db.Db.Outbox.Add(new OutboxRow
        {
            Id = Ids.New(),
            Type = "layout.location.created",
            Payload = json,
            OccurredAt = context.OccurredAt
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
